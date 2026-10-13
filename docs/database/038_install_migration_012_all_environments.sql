:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\037_cache_u8_document_master_data.sql
GO

USE [LabelSystem_Test];
GO
:r .\037_cache_u8_document_master_data.sql
GO

USE [LabelSystem_Prod];
GO
:r .\037_cache_u8_document_master_data.sql
GO
