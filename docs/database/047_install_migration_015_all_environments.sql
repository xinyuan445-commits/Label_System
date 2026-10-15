:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\046_add_traceback_mirror_and_sync_tasks.sql
GO

USE [LabelSystem_Test];
GO
:r .\046_add_traceback_mirror_and_sync_tasks.sql
GO

USE [LabelSystem_Prod];
GO
:r .\046_add_traceback_mirror_and_sync_tasks.sql
GO
