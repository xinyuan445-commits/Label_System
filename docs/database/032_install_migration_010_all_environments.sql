:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\031_create_label_code_rules.sql
GO

USE [LabelSystem_Test];
GO
:r .\031_create_label_code_rules.sql
GO

USE [LabelSystem_Prod];
GO
:r .\031_create_label_code_rules.sql
GO
