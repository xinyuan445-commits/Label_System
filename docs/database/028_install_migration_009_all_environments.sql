:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\009_create_synced_document_drafts.sql
GO

USE [LabelSystem_Test];
GO
:r .\009_create_synced_document_drafts.sql
GO

USE [LabelSystem_Prod];
GO
:r .\009_create_synced_document_drafts.sql
GO
