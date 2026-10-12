/*
    对已经执行过 020_database_operations.sql 的环境补充长事务监测。
    新环境执行最新版 020 后，本脚本会安全跳过。
*/
USE msdb;
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS (SELECT 1 FROM dbo.sysjobs WHERE name = N'LabelSystem_LongTransactionCheck')
BEGIN
    DECLARE @JobId uniqueidentifier;

    EXEC dbo.sp_add_job
        @job_name = N'LabelSystem_LongTransactionCheck',
        @enabled = 1,
        @description = N'标签系统长事务检查；阈值为 5 分钟。',
        @owner_login_name = N'sa',
        @job_id = @JobId OUTPUT;

    EXEC dbo.sp_add_jobstep
        @job_id = @JobId,
        @step_name = N'CheckLongTransactions',
        @subsystem = N'TSQL',
        @database_name = N'master',
        @on_success_action = 1,
        @on_fail_action = 2,
        @command = N'
SET NOCOUNT ON;
DECLARE @Message nvarchar(2048);
SELECT @Message = STRING_AGG(
    CONVERT(nvarchar(max), CONCAT(
        N''数据库='', DB_NAME(dt.database_id),
        N'', 会话='', st.session_id,
        N'', 已持续分钟='', DATEDIFF(MINUTE, at.transaction_begin_time, SYSDATETIME()),
        N'', 登录='', COALESCE(s.login_name, N''UNKNOWN''))), N''；'')
FROM sys.dm_tran_session_transactions AS st
INNER JOIN sys.dm_tran_active_transactions AS at
    ON at.transaction_id = st.transaction_id
INNER JOIN sys.dm_tran_database_transactions AS dt
    ON dt.transaction_id = st.transaction_id
LEFT JOIN sys.dm_exec_sessions AS s
    ON s.session_id = st.session_id
WHERE dt.database_id IN
    (DB_ID(N''LabelSystem_Dev''), DB_ID(N''LabelSystem_Test''), DB_ID(N''LabelSystem_Prod''))
  AND s.is_user_process = 1
  AND DATEDIFF(MINUTE, at.transaction_begin_time, SYSDATETIME()) >= 5;

IF @Message IS NOT NULL
BEGIN
    SET @Message = N''LabelSystem 长事务预警：'' + @Message;
    RAISERROR(@Message, 16, 1) WITH LOG;
END;';

    EXEC dbo.sp_add_jobschedule
        @job_id = @JobId,
        @name = N'Every_5_Minutes',
        @freq_type = 4,
        @freq_interval = 1,
        @freq_subday_type = 4,
        @freq_subday_interval = 5,
        @active_start_time = 000000;
    EXEC dbo.sp_add_jobserver @job_id = @JobId;
END;

