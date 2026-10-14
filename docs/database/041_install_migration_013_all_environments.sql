:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\040_add_generated_label_to_operation_line.sql
GO

USE [LabelSystem_Test];
GO
:r .\040_add_generated_label_to_operation_line.sql
GO

USE [LabelSystem_Prod];
GO
:r .\040_add_generated_label_to_operation_line.sql
GO
