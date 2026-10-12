USE master;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Checksum char(64) = '6516EE50A50C593A018ABE61E372C716A0BE6BE1F75AE64C3F05070070D1C8B9';
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
        IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = ''010'')
            THROW 50001, N''缺少 010 迁移记录。'', 1;
        UPDATE dbo.schema_migration
        SET script_checksum = @Checksum
        WHERE migration_id = ''010''
          AND (script_checksum IS NULL OR script_checksum = @Checksum);
        IF @@ROWCOUNT <> 1
            THROW 50002, N''010 迁移已经记录了不同的脚本哈希。'', 1;';
    EXEC sys.sp_executesql @Sql, N'@Checksum char(64)', @Checksum = @Checksum;
    FETCH NEXT FROM database_cursor INTO @DatabaseName;
END;
CLOSE database_cursor;
DEALLOCATE database_cursor;
