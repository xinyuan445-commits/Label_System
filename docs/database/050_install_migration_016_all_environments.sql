:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\049_cache_u8_materials.sql
GO
USE [LabelSystem_Test];
GO
:r .\049_cache_u8_materials.sql
GO
USE [LabelSystem_Prod];
GO
:r .\049_cache_u8_materials.sql
GO
