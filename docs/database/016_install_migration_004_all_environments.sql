:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\004_fix_reference_data_sync_time_constraint.sql
GO

USE [LabelSystem_Test];
GO
:r .\004_fix_reference_data_sync_time_constraint.sql
GO

USE [LabelSystem_Prod];
GO
:r .\004_fix_reference_data_sync_time_constraint.sql
GO
