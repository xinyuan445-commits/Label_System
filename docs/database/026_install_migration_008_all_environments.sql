:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\008_create_user_admin_audit.sql
GO

USE [LabelSystem_Test];
GO
:r .\008_create_user_admin_audit.sql
GO

USE [LabelSystem_Prod];
GO
:r .\008_create_user_admin_audit.sql
GO
