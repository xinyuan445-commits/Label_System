:ON ERROR EXIT

USE [LabelSystem_Dev];
GO
:r .\043_defer_product_label_code_until_print.sql
GO

USE [LabelSystem_Test];
GO
:r .\043_defer_product_label_code_until_print.sql
GO

USE [LabelSystem_Prod];
GO
:r .\043_defer_product_label_code_until_print.sql
GO
