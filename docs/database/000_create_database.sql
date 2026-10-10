/*
    标签管理系统数据库创建脚本

    默认创建本地开发数据库 LabelSystem_Dev。
    执行前只需确认下面三个变量；脚本不会创建登录名、保存密码或执行建表脚本。

    development / test 建议使用 SIMPLE 恢复模式。
    production 建议使用 FULL，并在正式写入前配置完整备份和日志备份任务。
*/
USE master;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DatabaseName sysname = N'LabelSystem_Dev';
DECLARE @Environment varchar(20) = 'development';
DECLARE @RecoveryModel varchar(10) = 'SIMPLE';

IF @DatabaseName IS NULL
   OR LTRIM(RTRIM(@DatabaseName)) = N''
   OR @DatabaseName IN (N'master', N'model', N'msdb', N'tempdb')
BEGIN
    THROW 50001, N'数据库名称为空或使用了 SQL Server 系统数据库名称。', 1;
END;

IF @Environment NOT IN ('development', 'test', 'production')
BEGIN
    THROW 50002, N'Environment 只能是 development、test 或 production。', 1;
END;

IF @RecoveryModel NOT IN ('SIMPLE', 'FULL')
BEGIN
    THROW 50003, N'RecoveryModel 只能是 SIMPLE 或 FULL。', 1;
END;

IF @Environment = 'production' AND @RecoveryModel <> 'FULL'
BEGIN
    THROW 50004, N'生产环境必须使用 FULL 恢复模式，并配置完整备份和日志备份。', 1;
END;

IF DB_ID(@DatabaseName) IS NOT NULL
BEGIN
    THROW 50005, N'同名数据库已经存在，脚本已停止，不会覆盖现有数据库。', 1;
END;

DECLARE @QuotedDatabaseName nvarchar(258) = QUOTENAME(@DatabaseName);
DECLARE @Sql nvarchar(max);

/*
    不写死 MDF/LDF 文件路径，使用当前 SQL Server 实例的默认数据和日志目录。
    Chinese_PRC_CI_AS 与常见 U8 中文账套排序规则兼容；业务文本仍统一使用 nvarchar。
*/
SET @Sql = N'CREATE DATABASE ' + @QuotedDatabaseName
         + N' COLLATE Chinese_PRC_CI_AS;';
EXEC sys.sp_executesql @Sql;

/* 新数据库启用行版本读取，减少查询阻塞业务写入。 */
SET @Sql = N'ALTER DATABASE ' + @QuotedDatabaseName + N' SET AUTO_CLOSE OFF;'
         + N'ALTER DATABASE ' + @QuotedDatabaseName + N' SET AUTO_SHRINK OFF;'
         + N'ALTER DATABASE ' + @QuotedDatabaseName + N' SET PAGE_VERIFY CHECKSUM;'
         + N'ALTER DATABASE ' + @QuotedDatabaseName + N' SET ALLOW_SNAPSHOT_ISOLATION ON;'
         + N'ALTER DATABASE ' + @QuotedDatabaseName + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;'
         + N'ALTER DATABASE ' + @QuotedDatabaseName + N' SET RECOVERY ' + @RecoveryModel + N';';
EXEC sys.sp_executesql @Sql;

/* 写入数据库级说明，便于在 SSMS 中识别用途和环境。 */
DECLARE @Description nvarchar(4000) =
    N'标签管理系统业务数据库；保存标签、托盘、业务操作、打印、配置和审计数据，不保存 U8 业务表副本或账号密码。';

SET @Sql = N'USE ' + @QuotedDatabaseName + N';'
         + N'EXEC sys.sp_addextendedproperty '
         + N'@name=N''MS_Description'', @value=@Description;'
         + N'EXEC sys.sp_addextendedproperty '
         + N'@name=N''ApplicationName'', @value=N''Label System'';'
         + N'EXEC sys.sp_addextendedproperty '
         + N'@name=N''Environment'', @value=@Environment;';

EXEC sys.sp_executesql
    @Sql,
    N'@Description nvarchar(4000), @Environment varchar(20)',
    @Description = @Description,
    @Environment = @Environment;

SELECT
    d.name AS database_name,
    d.collation_name,
    d.recovery_model_desc,
    d.snapshot_isolation_state_desc,
    d.is_read_committed_snapshot_on,
    d.page_verify_option_desc,
    @Environment AS application_environment
FROM sys.databases AS d
WHERE d.name = @DatabaseName;

/*
    下一步：
    1. 明确切换到刚创建的数据库。
    2. 评审通过后执行 001_label_business_schema_draft.sql。
    3. 为业务 API 创建最小权限专用登录名和数据库用户，不使用 sa 作为应用连接账号。
*/
