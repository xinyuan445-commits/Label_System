:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\034_allow_shared_inventory_label_rule.sql
GO

USE [LabelSystem_Test];
GO
:r .\034_allow_shared_inventory_label_rule.sql
GO

USE [LabelSystem_Prod];
GO
:r .\034_allow_shared_inventory_label_rule.sql
GO
