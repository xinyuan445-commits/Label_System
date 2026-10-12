/*
    标签管理系统数据库运行保障配置

    内容：
    1. 启用 Query Store，用于慢查询和执行计划追踪。
    2. 创建 SQL Server Agent 作业：完整备份、生产日志备份、完整性检查、
       索引/统计维护、备份清理、磁盘容量检查。
    3. 不配置邮件收件人；容量检查失败会写入 SQL Server 错误日志和作业历史。

    前提：000 和 001 已成功执行，SQL Server Agent 正在运行。
*/
USE master;
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_ID(N'LabelSystem_Dev') IS NULL
   OR DB_ID(N'LabelSystem_Test') IS NULL
   OR DB_ID(N'LabelSystem_Prod') IS NULL
BEGIN
    THROW 50001, N'标签系统数据库不完整，请先执行 000_create_database.sql。', 1;
END;

IF EXISTS
(
    SELECT 1
    FROM msdb.dbo.sysjobs
    WHERE name IN
    (
        N'LabelSystem_FullBackup',
        N'LabelSystem_ProdLogBackup',
        N'LabelSystem_CheckDb',
        N'LabelSystem_IndexAndStatistics',
        N'LabelSystem_BackupCleanup',
        N'LabelSystem_DiskCapacityCheck',
        N'LabelSystem_LongTransactionCheck'
    )
)
BEGIN
    THROW 50002, N'标签系统维护作业已经存在，脚本已停止，不会覆盖现有作业。', 1;
END;

EXEC master.dbo.xp_create_subdir N'D:\LabelSystem\Backup';
EXEC master.dbo.xp_create_subdir N'D:\LabelSystem\Backup\Full';
EXEC master.dbo.xp_create_subdir N'D:\LabelSystem\Backup\Log';

ALTER DATABASE [LabelSystem_Dev] SET QUERY_STORE = ON;
ALTER DATABASE [LabelSystem_Dev] SET QUERY_STORE
(
    OPERATION_MODE = READ_WRITE,
    QUERY_CAPTURE_MODE = AUTO,
    MAX_STORAGE_SIZE_MB = 256,
    CLEANUP_POLICY = (STALE_QUERY_THRESHOLD_DAYS = 30),
    WAIT_STATS_CAPTURE_MODE = ON
);

ALTER DATABASE [LabelSystem_Test] SET QUERY_STORE = ON;
ALTER DATABASE [LabelSystem_Test] SET QUERY_STORE
(
    OPERATION_MODE = READ_WRITE,
    QUERY_CAPTURE_MODE = AUTO,
    MAX_STORAGE_SIZE_MB = 512,
    CLEANUP_POLICY = (STALE_QUERY_THRESHOLD_DAYS = 60),
    WAIT_STATS_CAPTURE_MODE = ON
);

ALTER DATABASE [LabelSystem_Prod] SET QUERY_STORE = ON;
ALTER DATABASE [LabelSystem_Prod] SET QUERY_STORE
(
    OPERATION_MODE = READ_WRITE,
    QUERY_CAPTURE_MODE = AUTO,
    MAX_STORAGE_SIZE_MB = 1024,
    CLEANUP_POLICY = (STALE_QUERY_THRESHOLD_DAYS = 90),
    WAIT_STATS_CAPTURE_MODE = ON
);

DECLARE @JobId uniqueidentifier;

/* 每日 01:30：三套数据库完整备份，并立即执行 RESTORE VERIFYONLY。 */
EXEC msdb.dbo.sp_add_job
    @job_name = N'LabelSystem_FullBackup',
    @enabled = 1,
    @description = N'标签系统三套数据库每日完整备份并校验。',
    @owner_login_name = N'sa',
    @job_id = @JobId OUTPUT;

EXEC msdb.dbo.sp_add_jobstep
    @job_id = @JobId,
    @step_name = N'BackupAndVerify',
    @subsystem = N'TSQL',
    @database_name = N'master',
    @on_success_action = 1,
    @on_fail_action = 2,
    @command = N'
SET NOCOUNT ON;
DECLARE @Now datetime2(0) = DATEADD(HOUR, 8, SYSUTCDATETIME());
DECLARE @Stamp varchar(20) = CONVERT(char(8), @Now, 112) + ''_'' + REPLACE(CONVERT(char(8), @Now, 108), '':'', '''');
DECLARE @File nvarchar(260);

SET @File = N''D:\LabelSystem\Backup\Full\LabelSystem_Dev_'' + @Stamp + N''.bak'';
BACKUP DATABASE [LabelSystem_Dev] TO DISK = @File WITH COMPRESSION, CHECKSUM;
RESTORE VERIFYONLY FROM DISK = @File WITH CHECKSUM;

SET @File = N''D:\LabelSystem\Backup\Full\LabelSystem_Test_'' + @Stamp + N''.bak'';
BACKUP DATABASE [LabelSystem_Test] TO DISK = @File WITH COMPRESSION, CHECKSUM;
RESTORE VERIFYONLY FROM DISK = @File WITH CHECKSUM;

SET @File = N''D:\LabelSystem\Backup\Full\LabelSystem_Prod_'' + @Stamp + N''.bak'';
BACKUP DATABASE [LabelSystem_Prod] TO DISK = @File WITH COMPRESSION, CHECKSUM;
RESTORE VERIFYONLY FROM DISK = @File WITH CHECKSUM;';

EXEC msdb.dbo.sp_add_jobschedule
    @job_id = @JobId,
    @name = N'Daily_0130',
    @freq_type = 4,
    @freq_interval = 1,
    @active_start_time = 013000;
EXEC msdb.dbo.sp_add_jobserver @job_id = @JobId;

/* 每 15 分钟：生产库事务日志备份，RPO 目标 15 分钟。 */
SET @JobId = NULL;
EXEC msdb.dbo.sp_add_job
    @job_name = N'LabelSystem_ProdLogBackup',
    @enabled = 1,
    @description = N'标签系统生产数据库每 15 分钟事务日志备份。',
    @owner_login_name = N'sa',
    @job_id = @JobId OUTPUT;

EXEC msdb.dbo.sp_add_jobstep
    @job_id = @JobId,
    @step_name = N'BackupLog',
    @subsystem = N'TSQL',
    @database_name = N'master',
    @on_success_action = 1,
    @on_fail_action = 2,
    @command = N'
SET NOCOUNT ON;
DECLARE @Now datetime2(0) = DATEADD(HOUR, 8, SYSUTCDATETIME());
DECLARE @Stamp varchar(20) = CONVERT(char(8), @Now, 112) + ''_'' + REPLACE(CONVERT(char(8), @Now, 108), '':'', '''');
DECLARE @File nvarchar(260) = N''D:\LabelSystem\Backup\Log\LabelSystem_Prod_'' + @Stamp + N''.trn'';
BACKUP LOG [LabelSystem_Prod] TO DISK = @File WITH COMPRESSION, CHECKSUM;';

EXEC msdb.dbo.sp_add_jobschedule
    @job_id = @JobId,
    @name = N'Every_15_Minutes',
    @freq_type = 4,
    @freq_interval = 1,
    @freq_subday_type = 4,
    @freq_subday_interval = 15,
    @active_start_time = 000000;
EXEC msdb.dbo.sp_add_jobserver @job_id = @JobId;

/* 每周日 03:00：完整 DBCC CHECKDB。 */
SET @JobId = NULL;
EXEC msdb.dbo.sp_add_job
    @job_name = N'LabelSystem_CheckDb',
    @enabled = 1,
    @description = N'标签系统三套数据库每周完整性检查。',
    @owner_login_name = N'sa',
    @job_id = @JobId OUTPUT;

EXEC msdb.dbo.sp_add_jobstep
    @job_id = @JobId,
    @step_name = N'CheckDatabases',
    @subsystem = N'TSQL',
    @database_name = N'master',
    @on_success_action = 1,
    @on_fail_action = 2,
    @command = N'
DBCC CHECKDB ([LabelSystem_Dev]) WITH NO_INFOMSGS, ALL_ERRORMSGS;
DBCC CHECKDB ([LabelSystem_Test]) WITH NO_INFOMSGS, ALL_ERRORMSGS;
DBCC CHECKDB ([LabelSystem_Prod]) WITH NO_INFOMSGS, ALL_ERRORMSGS;';

EXEC msdb.dbo.sp_add_jobschedule
    @job_id = @JobId,
    @name = N'Weekly_Sunday_0300',
    @freq_type = 8,
    @freq_interval = 1,
    @freq_recurrence_factor = 1,
    @active_start_time = 030000;
EXEC msdb.dbo.sp_add_jobserver @job_id = @JobId;

/* 每周日 04:00：更新统计信息；索引达到阈值时整理或重建。 */
SET @JobId = NULL;
EXEC msdb.dbo.sp_add_job
    @job_name = N'LabelSystem_IndexAndStatistics',
    @enabled = 1,
    @description = N'标签系统每周索引与统计信息维护。',
    @owner_login_name = N'sa',
    @job_id = @JobId OUTPUT;

EXEC msdb.dbo.sp_add_jobstep
    @job_id = @JobId,
    @step_name = N'MaintainDatabases',
    @subsystem = N'TSQL',
    @database_name = N'master',
    @on_success_action = 1,
    @on_fail_action = 2,
    @command = N'
SET NOCOUNT ON;
DECLARE @DatabaseName sysname;
DECLARE database_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT name FROM sys.databases
WHERE name IN (N''LabelSystem_Dev'', N''LabelSystem_Test'', N''LabelSystem_Prod'')
  AND state_desc = ''ONLINE'';
OPEN database_cursor;
FETCH NEXT FROM database_cursor INTO @DatabaseName;
WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE @Sql nvarchar(max) = N''USE '' + QUOTENAME(@DatabaseName) + N'';
        DECLARE @SchemaName sysname, @TableName sysname, @IndexName sysname, @Fragmentation float;
        DECLARE index_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT s.name, t.name, i.name, ps.avg_fragmentation_in_percent
        FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, ''''LIMITED'''') AS ps
        INNER JOIN sys.indexes AS i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
        INNER JOIN sys.tables AS t ON t.object_id = ps.object_id
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        WHERE ps.page_count >= 1000
          AND ps.avg_fragmentation_in_percent >= 10
          AND i.index_id > 0
          AND i.name IS NOT NULL;
        OPEN index_cursor;
        FETCH NEXT FROM index_cursor INTO @SchemaName, @TableName, @IndexName, @Fragmentation;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            DECLARE @IndexSql nvarchar(max) = N''''ALTER INDEX '''' + QUOTENAME(@IndexName)
                + N'''' ON '''' + QUOTENAME(@SchemaName) + N''''.'''' + QUOTENAME(@TableName)
                + CASE WHEN @Fragmentation >= 30 THEN N'''' REBUILD;'''' ELSE N'''' REORGANIZE;'''' END;
            EXEC sys.sp_executesql @IndexSql;
            FETCH NEXT FROM index_cursor INTO @SchemaName, @TableName, @IndexName, @Fragmentation;
        END;
        CLOSE index_cursor;
        DEALLOCATE index_cursor;
        EXEC sys.sp_updatestats;'';
    EXEC sys.sp_executesql @Sql;
    FETCH NEXT FROM database_cursor INTO @DatabaseName;
END;
CLOSE database_cursor;
DEALLOCATE database_cursor;';

EXEC msdb.dbo.sp_add_jobschedule
    @job_id = @JobId,
    @name = N'Weekly_Sunday_0400',
    @freq_type = 8,
    @freq_interval = 1,
    @freq_recurrence_factor = 1,
    @active_start_time = 040000;
EXEC msdb.dbo.sp_add_jobserver @job_id = @JobId;

/* 每日 05:00：清理 30 天以前的 .bak 和 .trn。 */
SET @JobId = NULL;
EXEC msdb.dbo.sp_add_job
    @job_name = N'LabelSystem_BackupCleanup',
    @enabled = 1,
    @description = N'清理标签系统 30 天以前的数据库备份。',
    @owner_login_name = N'sa',
    @job_id = @JobId OUTPUT;

EXEC msdb.dbo.sp_add_jobstep
    @job_id = @JobId,
    @step_name = N'Cleanup',
    @subsystem = N'TSQL',
    @database_name = N'master',
    @on_success_action = 1,
    @on_fail_action = 2,
    @command = N'
DECLARE @Cutoff datetime = DATEADD(DAY, -30, DATEADD(HOUR, 8, SYSUTCDATETIME()));
EXEC master.dbo.xp_delete_file 0, N''D:\LabelSystem\Backup\Full'', N''bak'', @Cutoff, 0;
EXEC master.dbo.xp_delete_file 0, N''D:\LabelSystem\Backup\Log'', N''trn'', @Cutoff, 0;';

EXEC msdb.dbo.sp_add_jobschedule
    @job_id = @JobId,
    @name = N'Daily_0500',
    @freq_type = 4,
    @freq_interval = 1,
    @active_start_time = 050000;
EXEC msdb.dbo.sp_add_jobserver @job_id = @JobId;

/* 每 15 分钟：任一数据库文件所在盘低于 20% 或 50GB 时使作业失败并写错误日志。 */
SET @JobId = NULL;
EXEC msdb.dbo.sp_add_job
    @job_name = N'LabelSystem_DiskCapacityCheck',
    @enabled = 1,
    @description = N'标签系统磁盘容量检查；阈值为剩余 20% 或 50GB。',
    @owner_login_name = N'sa',
    @job_id = @JobId OUTPUT;

EXEC msdb.dbo.sp_add_jobstep
    @job_id = @JobId,
    @step_name = N'CheckDiskSpace',
    @subsystem = N'TSQL',
    @database_name = N'master',
    @on_success_action = 1,
    @on_fail_action = 2,
    @command = N'
SET NOCOUNT ON;
DECLARE @Message nvarchar(2048);
SELECT @Message = STRING_AGG(
    CONVERT(nvarchar(max), CONCAT(volume_mount_point, N'' 剩余 '',
        CAST(available_bytes / 1073741824.0 AS decimal(18,2)), N''GB / '',
        CAST(total_bytes / 1073741824.0 AS decimal(18,2)), N''GB'')), N''；'')
FROM
(
    SELECT DISTINCT vs.volume_mount_point, vs.available_bytes, vs.total_bytes
    FROM sys.master_files AS mf
    CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) AS vs
    WHERE DB_NAME(mf.database_id) IN
        (N''tempdb'', N''LabelSystem_Dev'', N''LabelSystem_Test'', N''LabelSystem_Prod'')
      AND
      (
          vs.available_bytes * 1.0 / NULLIF(vs.total_bytes, 0) < 0.20
          OR vs.available_bytes < 50 * 1073741824.0
      )
) AS warning_volumes;

IF @Message IS NOT NULL
BEGIN
    SET @Message = N''LabelSystem 磁盘容量预警：'' + @Message;
    RAISERROR(@Message, 16, 1) WITH LOG;
END;';

EXEC msdb.dbo.sp_add_jobschedule
    @job_id = @JobId,
    @name = N'Every_15_Minutes',
    @freq_type = 4,
    @freq_interval = 1,
    @freq_subday_type = 4,
    @freq_subday_interval = 15,
    @active_start_time = 000000;
EXEC msdb.dbo.sp_add_jobserver @job_id = @JobId;

/* 每 5 分钟：标签数据库存在超过 5 分钟的用户事务时失败并写错误日志。 */
SET @JobId = NULL;
EXEC msdb.dbo.sp_add_job
    @job_name = N'LabelSystem_LongTransactionCheck',
    @enabled = 1,
    @description = N'标签系统长事务检查；阈值为 5 分钟。',
    @owner_login_name = N'sa',
    @job_id = @JobId OUTPUT;

EXEC msdb.dbo.sp_add_jobstep
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

EXEC msdb.dbo.sp_add_jobschedule
    @job_id = @JobId,
    @name = N'Every_5_Minutes',
    @freq_type = 4,
    @freq_interval = 1,
    @freq_subday_type = 4,
    @freq_subday_interval = 5,
    @active_start_time = 000000;
EXEC msdb.dbo.sp_add_jobserver @job_id = @JobId;

/* 立即建立三套数据库的首个完整备份，启动生产日志备份链。 */
DECLARE @Now datetime2(0) = DATEADD(HOUR, 8, SYSUTCDATETIME());
DECLARE @Stamp varchar(20) = CONVERT(char(8), @Now, 112) + '_' + REPLACE(CONVERT(char(8), @Now, 108), ':', '');
DECLARE @File nvarchar(260);

SET @File = N'D:\LabelSystem\Backup\Full\LabelSystem_Dev_' + @Stamp + N'.bak';
BACKUP DATABASE [LabelSystem_Dev] TO DISK = @File WITH COMPRESSION, CHECKSUM;
RESTORE VERIFYONLY FROM DISK = @File WITH CHECKSUM;

SET @File = N'D:\LabelSystem\Backup\Full\LabelSystem_Test_' + @Stamp + N'.bak';
BACKUP DATABASE [LabelSystem_Test] TO DISK = @File WITH COMPRESSION, CHECKSUM;
RESTORE VERIFYONLY FROM DISK = @File WITH CHECKSUM;

SET @File = N'D:\LabelSystem\Backup\Full\LabelSystem_Prod_' + @Stamp + N'.bak';
BACKUP DATABASE [LabelSystem_Prod] TO DISK = @File WITH COMPRESSION, CHECKSUM;
RESTORE VERIFYONLY FROM DISK = @File WITH CHECKSUM;

SELECT name, enabled, date_created
FROM msdb.dbo.sysjobs
WHERE name LIKE N'LabelSystem[_]%'
ORDER BY name;
