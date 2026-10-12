:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\006_create_users_permissions_preferences.sql
GO

USE [LabelSystem_Test];
GO
:r .\006_create_users_permissions_preferences.sql
GO

USE [LabelSystem_Prod];
GO
:r .\006_create_users_permissions_preferences.sql
GO
