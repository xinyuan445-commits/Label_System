/*
    记录 001 初始迁移脚本的 SHA-256。
    哈希对应仓库文件：001_label_business_schema_draft.sql
*/
USE master;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Checksum char(64) = 'E9A76F7187D34748AD923C47CFC960A9C66B17FEAD0A5BB2B527D79062F0137F';
DECLARE @DatabaseName sysname;
DECLARE @Sql nvarchar(max);

DECLARE database_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT name
FROM sys.databases
WHERE name IN (N'LabelSystem_Dev', N'LabelSystem_Test', N'LabelSystem_Prod')
ORDER BY name;

OPEN database_cursor;
FETCH NEXT FROM database_cursor INTO @DatabaseName;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @Sql = N'USE ' + QUOTENAME(@DatabaseName) + N';
        IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = ''001'')
            THROW 50001, N''缺少 001 迁移记录。'', 1;

        IF EXISTS
        (
            SELECT 1
            FROM dbo.schema_migration
            WHERE migration_id = ''001''
              AND script_checksum IS NOT NULL
              AND script_checksum <> @Checksum
        )
            THROW 50002, N''001 迁移已经记录了不同的脚本哈希。'', 1;

        UPDATE dbo.schema_migration
        SET script_checksum = @Checksum
        WHERE migration_id = ''001'';';

    EXEC sys.sp_executesql @Sql, N'@Checksum char(64)', @Checksum = @Checksum;
    FETCH NEXT FROM database_cursor INTO @DatabaseName;
END;

CLOSE database_cursor;
DEALLOCATE database_cursor;

