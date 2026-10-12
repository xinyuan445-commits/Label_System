/*
    使用 sqlcmd 执行本脚本，把 001 初始迁移安装到开发、测试和生产数据库。
    必须先成功执行 000_create_database.sql。

    示例：
    sqlcmd -S 172.16.1.159 -U sa -d master -b -f 65001 -i 010_install_schema_all_environments.sql
*/
:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\001_label_business_schema_draft.sql
GO

USE [LabelSystem_Test];
GO
:r .\001_label_business_schema_draft.sql
GO

USE [LabelSystem_Prod];
GO
:r .\001_label_business_schema_draft.sql
GO
