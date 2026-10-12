/*
    标签管理系统数据库创建脚本

    目标实例：Microsoft SQL Server 2019（兼容级别 150）
    排序规则：Chinese_PRC_CI_AS，与当前 U8 账套一致
    文件目录：D:\LabelSystem\Data、D:\LabelSystem\Log

    本脚本一次创建开发、测试、生产三套数据库，不覆盖同名数据库：
      LabelSystem_Dev   - development / SIMPLE
      LabelSystem_Test  - test        / SIMPLE
      LabelSystem_Prod  - production  / FULL

    当前项目决定使用 sa 连接数据库。本脚本把数据库 owner 固定为 sa，
    但不会把 sa 密码写入数据库、脚本或业务配置表。

    旧版 sqlcmd 执行参数必须包含：-b -f 65001
*/
USE master;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DataPath nvarchar(260) = N'D:\LabelSystem\Data\';
DECLARE @LogPath nvarchar(260) = N'D:\LabelSystem\Log\';

/* 由 SQL Server 服务账号创建目录，避免依赖人工预建目录。 */
EXEC master.dbo.xp_create_subdir N'D:\LabelSystem';
EXEC master.dbo.xp_create_subdir N'D:\LabelSystem\Data';
EXEC master.dbo.xp_create_subdir N'D:\LabelSystem\Log';
EXEC master.dbo.xp_create_subdir N'D:\LabelSystem\Backup';
EXEC master.dbo.xp_create_subdir N'D:\LabelSystem\Backup\Full';
EXEC master.dbo.xp_create_subdir N'D:\LabelSystem\Backup\Log';

DECLARE @Databases TABLE
(
    database_name        sysname NOT NULL,
    environment_name     varchar(20) NOT NULL,
    recovery_model       varchar(10) NOT NULL,
    data_initial_mb      int NOT NULL,
    data_growth_mb       int NOT NULL,
    data_max_mb          int NOT NULL,
    log_initial_mb       int NOT NULL,
    log_growth_mb        int NOT NULL,
    log_max_mb           int NOT NULL
);

/*
    MAXSIZE 合计控制在约 295GB，按当前 D 盘容量至少保留约 20% 空间。
    增长量全部使用固定 MB，避免百分比增长随文件变大而突然放大。
*/
INSERT INTO @Databases
(
    database_name, environment_name, recovery_model,
    data_initial_mb, data_growth_mb, data_max_mb,
    log_initial_mb, log_growth_mb, log_max_mb
)
VALUES
    (N'LabelSystem_Dev',  'development', 'SIMPLE',  512, 128,  20480,  256,  64,  4096),
    (N'LabelSystem_Test', 'test',        'SIMPLE', 1024, 256,  40960,  512, 128,  8192),
    (N'LabelSystem_Prod', 'production',  'FULL',   4096, 512, 184320, 1024, 256, 40960);

IF EXISTS
(
    SELECT 1
    FROM @Databases AS wanted
    INNER JOIN sys.databases AS existing ON existing.name = wanted.database_name
)
BEGIN
    THROW 50001, N'至少一个目标数据库已经存在，脚本已停止，不会覆盖或修改现有数据库。', 1;
END;

DECLARE
    @DatabaseName sysname,
    @Environment varchar(20),
    @RecoveryModel varchar(10),
    @DataInitialMb int,
    @DataGrowthMb int,
    @DataMaxMb int,
    @LogInitialMb int,
    @LogGrowthMb int,
    @LogMaxMb int,
    @Sql nvarchar(max),
    @QuotedDatabaseName nvarchar(258),
    @DataLogicalName sysname,
    @LogLogicalName sysname,
    @Description nvarchar(4000);

DECLARE database_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT
    database_name, environment_name, recovery_model,
    data_initial_mb, data_growth_mb, data_max_mb,
    log_initial_mb, log_growth_mb, log_max_mb
FROM @Databases
ORDER BY CASE environment_name
             WHEN 'development' THEN 1
             WHEN 'test' THEN 2
             ELSE 3
         END;

OPEN database_cursor;
FETCH NEXT FROM database_cursor INTO
    @DatabaseName, @Environment, @RecoveryModel,
    @DataInitialMb, @DataGrowthMb, @DataMaxMb,
    @LogInitialMb, @LogGrowthMb, @LogMaxMb;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @QuotedDatabaseName = QUOTENAME(@DatabaseName);
    SET @DataLogicalName = @DatabaseName + N'_Data';
    SET @LogLogicalName = @DatabaseName + N'_Log';

    SET @Sql = N'CREATE DATABASE ' + @QuotedDatabaseName + N'
        ON PRIMARY
        (
            NAME = N''' + REPLACE(@DataLogicalName, '''', '''''') + N''',
            FILENAME = N''' + REPLACE(@DataPath + @DatabaseName + N'.mdf', '''', '''''') + N''',
            SIZE = ' + CONVERT(nvarchar(20), @DataInitialMb) + N'MB,
            FILEGROWTH = ' + CONVERT(nvarchar(20), @DataGrowthMb) + N'MB,
            MAXSIZE = ' + CONVERT(nvarchar(20), @DataMaxMb) + N'MB
        )
        LOG ON
        (
            NAME = N''' + REPLACE(@LogLogicalName, '''', '''''') + N''',
            FILENAME = N''' + REPLACE(@LogPath + @DatabaseName + N'.ldf', '''', '''''') + N''',
            SIZE = ' + CONVERT(nvarchar(20), @LogInitialMb) + N'MB,
            FILEGROWTH = ' + CONVERT(nvarchar(20), @LogGrowthMb) + N'MB,
            MAXSIZE = ' + CONVERT(nvarchar(20), @LogMaxMb) + N'MB
        )
        COLLATE Chinese_PRC_CI_AS;';
    EXEC sys.sp_executesql @Sql;

    SET @Sql = N'ALTER AUTHORIZATION ON DATABASE::' + @QuotedDatabaseName + N' TO sa;
        ALTER DATABASE ' + @QuotedDatabaseName + N' SET COMPATIBILITY_LEVEL = 150;
        ALTER DATABASE ' + @QuotedDatabaseName + N' SET AUTO_CLOSE OFF;
        ALTER DATABASE ' + @QuotedDatabaseName + N' SET AUTO_SHRINK OFF;
        ALTER DATABASE ' + @QuotedDatabaseName + N' SET PAGE_VERIFY CHECKSUM;
        ALTER DATABASE ' + @QuotedDatabaseName + N' SET TRUSTWORTHY OFF;
        ALTER DATABASE ' + @QuotedDatabaseName + N' SET DB_CHAINING OFF;
        ALTER DATABASE ' + @QuotedDatabaseName + N' SET ALLOW_SNAPSHOT_ISOLATION ON;
        ALTER DATABASE ' + @QuotedDatabaseName + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
        ALTER DATABASE ' + @QuotedDatabaseName + N' SET RECOVERY ' + @RecoveryModel + N';';
    EXEC sys.sp_executesql @Sql;

    SET @Description = N'标签管理系统业务数据库；环境：' + CONVERT(nvarchar(20), @Environment)
        + N'；保存标签、托盘、业务操作、配置和审计数据，不保存 U8 业务表副本或账号密码。';

    SET @Sql = N'USE ' + @QuotedDatabaseName + N';
        EXEC sys.sp_addextendedproperty @name=N''MS_Description'', @value=@Description;
        EXEC sys.sp_addextendedproperty @name=N''ApplicationName'', @value=N''Label System'';
        EXEC sys.sp_addextendedproperty @name=N''Environment'', @value=@Environment;';
    EXEC sys.sp_executesql
        @Sql,
        N'@Description nvarchar(4000), @Environment varchar(20)',
        @Description = @Description,
        @Environment = @Environment;

    FETCH NEXT FROM database_cursor INTO
        @DatabaseName, @Environment, @RecoveryModel,
        @DataInitialMb, @DataGrowthMb, @DataMaxMb,
        @LogInitialMb, @LogGrowthMb, @LogMaxMb;
END;

CLOSE database_cursor;
DEALLOCATE database_cursor;

SELECT
    d.name AS database_name,
    d.compatibility_level,
    d.collation_name,
    d.recovery_model_desc,
    d.snapshot_isolation_state_desc,
    d.is_read_committed_snapshot_on,
    d.page_verify_option_desc,
    SUSER_SNAME(d.owner_sid) AS owner_name,
    mf.type_desc,
    mf.physical_name,
    CAST(mf.size * 8.0 / 1024 AS decimal(18,2)) AS size_mb,
    CASE
        WHEN mf.is_percent_growth = 1 THEN CONCAT(mf.growth, N'%')
        ELSE CONCAT(CAST(mf.growth * 8.0 / 1024 AS decimal(18,2)), N' MB')
    END AS growth_setting,
    CASE
        WHEN mf.max_size = -1 THEN N'UNLIMITED'
        ELSE CONCAT(CAST(mf.max_size * 8.0 / 1024 AS decimal(18,2)), N' MB')
    END AS max_size_setting
FROM sys.databases AS d
INNER JOIN sys.master_files AS mf ON mf.database_id = d.database_id
WHERE d.name IN (N'LabelSystem_Dev', N'LabelSystem_Test', N'LabelSystem_Prod')
ORDER BY d.name, mf.type_desc;
