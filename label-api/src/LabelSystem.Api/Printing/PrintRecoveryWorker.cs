using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed partial class PrintRepository
{
    public Task<IReadOnlyList<JsonObject>> ClaimRecovery(CancellationToken ct)=>
        Transaction<IReadOnlyList<JsonObject>>(async db=>{
            // Short serializable lease transaction; no HTTP call or printer call holds row locks.
            var jobs=await db.Query("""
                SELECT TOP(10) o.print_job_id AS jobId,o.operator_id AS userId
                FROM dbo.business_operation o WITH(UPDLOCK,HOLDLOCK)
                WHERE o.print_scope=@scope AND o.print_job_id IS NOT NULL
                  AND EXISTS(SELECT 1 FROM dbo.traceback_sync_task t WITH(UPDLOCK,HOLDLOCK)
                    WHERE t.operation_id=o.id AND t.event_phase='PRINT_TARGET' AND t.status<>'COMPLETED'
                    AND (t.next_retry_at IS NULL OR t.next_retry_at<=DATEADD(HOUR,8,SYSUTCDATETIME())))
                ORDER BY o.id;
                """,("@scope",Scope));
            foreach(var job in jobs)await db.Execute("""
                UPDATE t SET next_retry_at=DATEADD(MINUTE,2,DATEADD(HOUR,8,SYSUTCDATETIME()))
                FROM dbo.traceback_sync_task t JOIN dbo.business_operation o ON o.id=t.operation_id
                WHERE o.print_job_id=@job AND t.event_phase='PRINT_TARGET' AND t.status<>'COMPLETED';
                """,("@job",Guid.Parse(job.S("jobId"))));
            return jobs;
        },ct);
}

public sealed class PrintRecoveryWorker(IServiceScopeFactory scopes,
    IOptionsMonitor<PrintingOptions> printing,IOptionsMonitor<LabelSystemOptions> labels,
    ILogger<PrintRecoveryWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Existing installations perform no print SQL or HTTP until explicitly configured.
            if(!printing.CurrentValue.Enabled||!printing.CurrentValue.RecoveryEnabled||
                !labels.CurrentValue.U8WritesEnabled)continue;
            try
            {
                using var scope=scopes.CreateScope();
                var provider=scope.ServiceProvider;
                var jobs=await provider.GetRequiredService<PrintRepository>().ClaimRecovery(stoppingToken);
                var users=provider.GetRequiredService<UserRepository>();
                foreach(var job in jobs)
                {
                    var user=await users.FindSessionUserByIdAsync(job.S("userId"),stoppingToken);
                    if(user is null||!await users.HasFunctionAccessAsync(user.Id,"print.confirm",stoppingToken))continue;
                    await provider.GetRequiredService<PrintService>().RetrySync(Guid.Parse(job.S("jobId")),user,stoppingToken);
                }
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception)
            {
                // Avoid logging SQL/configuration credentials or label payloads from exception messages.
                logger.LogWarning("打印登记自动恢复未完成，将保留原任务重试。");
            }
        }
    }
}
