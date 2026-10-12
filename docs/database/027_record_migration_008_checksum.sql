USE master;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Checksum char(64) = '610F583CBDDF68DB8A8D4C34F82CC8A510FC6E74C1BD2FE2B4BAEDB526B8C05C';
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
        IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = ''008'')
            THROW 50001, N''缺少 008 迁移记录。'', 1;
        UPDATE dbo.schema_migration
        SET script_checksum = @Checksum
        WHERE migration_id = ''008''
          AND (script_checksum IS NULL OR script_checksum = @Checksum);
        IF @@ROWCOUNT <> 1
            THROW 50002, N''008 迁移已经记录了不同的脚本哈希。'', 1;';
    EXEC sys.sp_executesql @Sql, N'@Checksum char(64)', @Checksum = @Checksum;
    FETCH NEXT FROM database_cursor INTO @DatabaseName;
END;
CLOSE database_cursor;
DEALLOCATE database_cursor;
