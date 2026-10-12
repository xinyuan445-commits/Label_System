:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\005_cache_u8_warehouse_position_department.sql
GO

USE [LabelSystem_Test];
GO
:r .\005_cache_u8_warehouse_position_department.sql
GO

USE [LabelSystem_Prod];
GO
:r .\005_cache_u8_warehouse_position_department.sql
GO
