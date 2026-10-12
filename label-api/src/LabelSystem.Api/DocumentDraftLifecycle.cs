using System.Data;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

internal static class DocumentDraftLifecycle
{
    public static string FromOperationStatus(string status) => status switch
    {
        "CANCELLED" => "CANCELLED",
        "SUCCESS" or "FAILED" or "REVERSED" => "COMPLETED",
        _ => "PREPARED"
    };

    public static async Task SetStatusAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string operationGroupId,
        string status,
        string actorUserId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var drafts = new List<(string Id, string PreviousStatus, long Revision)>();
        await using (var read = new SqlCommand("""
            SELECT draft_id, business_status, client_revision
            FROM dbo.document_draft WITH (UPDLOCK, HOLDLOCK)
            WHERE draft_id=@group_id OR operation_group_id=@group_id;
            """, connection, transaction))
        {
            read.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = operationGroupId;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                drafts.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }

        foreach (var draft in drafts)
        {
            await using (var update = new SqlCommand("""
                UPDATE dbo.document_draft
                SET business_status=@status, operation_group_id=@group_id,
                    last_synced_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                    deleted_at=CASE WHEN @status='DELETED' THEN DATEADD(HOUR,8,SYSUTCDATETIME()) ELSE NULL END
                WHERE draft_id=@draft_id;
                """, connection, transaction))
            {
                update.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = status;
                update.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = operationGroupId;
                update.Parameters.Add("@draft_id", SqlDbType.NVarChar, 100).Value = draft.Id;
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            if (draft.PreviousStatus == status) continue;
            await using var insert = new SqlCommand("""
                INSERT INTO dbo.document_draft_event
                    (draft_id, event_code, previous_status, current_status, actor_user_id,
                     device_id, client_revision, request_id)
                VALUES
                    (@draft_id, 'STATUS_CHANGED', @previous_status, @current_status, @actor_user_id,
                     NULL, @client_revision, @request_id);
                """, connection, transaction);
            insert.Parameters.Add("@draft_id", SqlDbType.NVarChar, 100).Value = draft.Id;
            insert.Parameters.Add("@previous_status", SqlDbType.VarChar, 20).Value = draft.PreviousStatus;
            insert.Parameters.Add("@current_status", SqlDbType.VarChar, 20).Value = status;
            insert.Parameters.Add("@actor_user_id", SqlDbType.NVarChar, 100).Value = actorUserId;
            insert.Parameters.Add("@client_revision", SqlDbType.BigInt).Value = draft.Revision;
            insert.Parameters.Add("@request_id", SqlDbType.NVarChar, 100).Value = requestId;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
