:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\002_seed_settings_and_source_reservation_index.sql
GO

USE [LabelSystem_Test];
GO
:r .\002_seed_settings_and_source_reservation_index.sql
GO

USE [LabelSystem_Prod];
GO
:r .\002_seed_settings_and_source_reservation_index.sql
GO

