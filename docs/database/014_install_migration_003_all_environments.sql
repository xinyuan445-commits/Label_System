:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\003_cache_u8_rd_style.sql
GO

USE [LabelSystem_Test];
GO
:r .\003_cache_u8_rd_style.sql
GO

USE [LabelSystem_Prod];
GO
:r .\003_cache_u8_rd_style.sql
GO
