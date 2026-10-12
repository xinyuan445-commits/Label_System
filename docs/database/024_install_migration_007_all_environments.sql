:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\007_add_local_user_credentials.sql
GO

USE [LabelSystem_Test];
GO
:r .\007_add_local_user_credentials.sql
GO

USE [LabelSystem_Prod];
GO
:r .\007_add_local_user_credentials.sql
GO

