/*
    标签系统生产库恢复演练

    从最近一次生产完整备份恢复到固定临时库 LabelSystem_RestoreCheck，
    执行 DBCC CHECKDB、表数量及迁移版本核对后删除临时库。
    本脚本不会覆盖开发、测试或生产数据库。
*/
USE master;
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_ID(N'LabelSystem_RestoreCheck') IS NOT NULL
BEGIN
    THROW 50001, N'临时恢复库 LabelSystem_RestoreCheck 已存在，脚本停止，未覆盖现有数据库。', 1;
END;

DECLARE @BackupFile nvarchar(260);

SELECT TOP (1)
    @BackupFile = bmf.physical_device_name
FROM msdb.dbo.backupset AS bs
INNER JOIN msdb.dbo.backupmediafamily AS bmf
    ON bmf.media_set_id = bs.media_set_id
WHERE bs.database_name = N'LabelSystem_Prod'
  AND bs.type = 'D'
  AND bs.is_copy_only = 0
ORDER BY bs.backup_finish_date DESC;

IF @BackupFile IS NULL
BEGIN
    THROW 50002, N'没有找到 LabelSystem_Prod 完整备份，无法执行恢复演练。', 1;
END;

EXEC master.dbo.xp_create_subdir N'D:\LabelSystem\Restore';

BEGIN TRY
    RESTORE DATABASE [LabelSystem_RestoreCheck]
    FROM DISK = @BackupFile
    WITH
        MOVE N'LabelSystem_Prod_Data' TO N'D:\LabelSystem\Restore\LabelSystem_RestoreCheck.mdf',
        MOVE N'LabelSystem_Prod_Log' TO N'D:\LabelSystem\Restore\LabelSystem_RestoreCheck.ldf',
        RECOVERY,
        CHECKSUM,
        STATS = 10;

    DBCC CHECKDB ([LabelSystem_RestoreCheck]) WITH NO_INFOMSGS, ALL_ERRORMSGS;

    DECLARE @ResultSql nvarchar(max) = N'
        SELECT
            DB_NAME() AS restored_database,
            COUNT(*) AS user_table_count
        FROM sys.tables;

        SELECT migration_id, description, script_checksum, applied_at, applied_by
        FROM dbo.schema_migration
        ORDER BY migration_id;';

    EXEC [LabelSystem_RestoreCheck].sys.sp_executesql @ResultSql;

    ALTER DATABASE [LabelSystem_RestoreCheck] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [LabelSystem_RestoreCheck];

    SELECT
        N'SUCCESS' AS restore_drill_result,
        @BackupFile AS verified_backup_file,
        DATEADD(HOUR, 8, SYSUTCDATETIME()) AS completed_at_beijing;
END TRY
BEGIN CATCH
    IF DB_ID(N'LabelSystem_RestoreCheck') IS NOT NULL
    BEGIN
        ALTER DATABASE [LabelSystem_RestoreCheck] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
        DROP DATABASE [LabelSystem_RestoreCheck];
    END;

    THROW;
END CATCH;
