/*
    UFDATA_888_2021 TraceBack 只读容量、重复值、索引和代表性执行计划核查。
    本脚本不创建索引，不修改数据；仅限测试账套执行。
*/
USE [UFDATA_888_2021];
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

IF DB_NAME() <> N'UFDATA_888_2021'
    THROW 50001, N'本脚本只允许在 UFDATA_888_2021 执行。', 1;

IF OBJECT_ID(N'dbo.TraceBackOnlyCode', N'U') IS NULL OR
   OBJECT_ID(N'dbo.TraceBackGoods', N'U') IS NULL OR
   OBJECT_ID(N'dbo.TraceBackOrder', N'U') IS NULL
    THROW 50002, N'TraceBack 三表不完整。', 1;

/* 数据量与保留空间。 */
SELECT
    s.name AS schema_name,
    t.name AS table_name,
    SUM(CASE WHEN p.index_id IN (0, 1) THEN p.row_count ELSE 0 END) AS row_count,
    CAST(SUM(p.reserved_page_count) * 8.0 / 1024 AS decimal(18,2)) AS reserved_mb
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.dm_db_partition_stats p ON p.object_id = t.object_id
WHERE s.name = N'dbo'
  AND t.name IN (N'TraceBackOnlyCode', N'TraceBackGoods', N'TraceBackOrder')
GROUP BY s.name, t.name
ORDER BY t.name;

/* 真实字段类型；用于确认查询参数不会触发索引列隐式转换。 */
SELECT
    t.name AS table_name,
    c.column_id,
    c.name AS column_name,
    ty.name AS data_type,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE s.name = N'dbo'
  AND t.name IN (N'TraceBackOnlyCode', N'TraceBackGoods', N'TraceBackOrder')
ORDER BY t.name, c.column_id;

/* 当前索引。 */
SELECT
    t.name AS table_name,
    i.name AS index_name,
    i.type_desc,
    i.is_unique,
    i.is_primary_key,
    i.has_filter,
    i.filter_definition,
    STRING_AGG(CASE WHEN ic.is_included_column = 0 THEN c.name END, N', ')
        WITHIN GROUP (ORDER BY ic.index_column_id) AS key_columns,
    STRING_AGG(CASE WHEN ic.is_included_column = 1 THEN c.name END, N', ')
        WITHIN GROUP (ORDER BY ic.index_column_id) AS included_columns
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.indexes i ON i.object_id = t.object_id AND i.index_id > 0
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE s.name = N'dbo'
  AND t.name IN (N'TraceBackOnlyCode', N'TraceBackGoods', N'TraceBackOrder')
GROUP BY t.name, i.name, i.type_desc, i.is_unique, i.is_primary_key,
         i.has_filter, i.filter_definition
ORDER BY t.name, i.name;

/* 关联键重复与空值。重复行不得直接用 UNIQUE 索引掩盖。 */
SELECT N'TraceBackGoods.pdaGoodsUuid' AS check_name,
       COUNT_BIG(*) AS duplicate_key_count,
       COALESCE(SUM(duplicate_rows), 0) AS duplicate_row_count
FROM
(
    SELECT COUNT_BIG(*) AS duplicate_rows
    FROM dbo.TraceBackGoods
    WHERE pdaGoodsUuid IS NOT NULL
    GROUP BY pdaGoodsUuid
    HAVING COUNT_BIG(*) > 1
) d
UNION ALL
SELECT N'TraceBackOrder.pdaOrderUuid', COUNT_BIG(*), COALESCE(SUM(duplicate_rows), 0)
FROM
(
    SELECT COUNT_BIG(*) AS duplicate_rows
    FROM dbo.TraceBackOrder
    WHERE pdaOrderUuid IS NOT NULL
    GROUP BY pdaOrderUuid
    HAVING COUNT_BIG(*) > 1
) d;

SELECT
    SUM(CASE WHEN traceBackCode IS NULL OR LTRIM(RTRIM(traceBackCode)) = N'' THEN 1 ELSE 0 END)
        AS empty_code_rows,
    SUM(CASE WHEN TRY_CONVERT(decimal(28,6), [sum]) IS NULL THEN 1 ELSE 0 END)
        AS invalid_quantity_rows,
    COUNT_BIG(DISTINCT traceBackCode) AS distinct_code_count
FROM dbo.TraceBackOnlyCode;

SELECT COUNT_BIG(*) AS negative_balance_code_count
FROM
(
    SELECT traceBackCode
    FROM dbo.TraceBackOnlyCode
    WHERE traceBackCode IS NOT NULL
    GROUP BY traceBackCode
    HAVING SUM(TRY_CONVERT(decimal(28,6), [sum])) < 0
) n;

/*
   代表性实时扫码。执行后在 SSMS 的“执行计划”页保存 Actual Plan，
   同时记录 Messages 中的 logical reads 和 elapsed time。
*/
DECLARE @SampleCode nvarchar(100) =
(
    SELECT TOP (1) CONVERT(nvarchar(100), traceBackCode)
    FROM dbo.TraceBackOnlyCode
    WHERE traceBackCode IS NOT NULL
    GROUP BY traceBackCode
    ORDER BY COUNT_BIG(*) DESC, MAX(id) DESC
);

SET STATISTICS IO ON;
SET STATISTICS TIME ON;
SET STATISTICS XML ON;

;WITH label_flow AS
(
    SELECT id, traceBackCode, [sum], pdaGoodsUuid, pdaOrderUuid
    FROM dbo.TraceBackOnlyCode
    WHERE traceBackCode = @SampleCode
), latest AS
(
    SELECT MAX(id) AS latest_id,
           SUM(TRY_CONVERT(decimal(28,6), [sum])) AS current_balance
    FROM label_flow
)
SELECT l.latest_id, l.current_balance, f.pdaGoodsUuid, f.pdaOrderUuid
FROM latest l
JOIN label_flow f ON f.id = l.latest_id;

SET STATISTICS XML OFF;
SET STATISTICS TIME OFF;
SET STATISTICS IO OFF;

/*
测试账套候选索引（本脚本不执行）：

CREATE NONCLUSTERED INDEX IX_TraceBackOnlyCode_traceBackCode_id
ON dbo.TraceBackOnlyCode(traceBackCode, id DESC)
INCLUDE([sum], pdaGoodsUuid, pdaOrderUuid)
WITH (SORT_IN_TEMPDB = ON, ONLINE = OFF);

CREATE NONCLUSTERED INDEX IX_TraceBackGoods_pdaGoodsUuid
ON dbo.TraceBackGoods(pdaGoodsUuid)
WITH (SORT_IN_TEMPDB = ON, ONLINE = OFF);

CREATE NONCLUSTERED INDEX IX_TraceBackOrder_pdaOrderUuid
ON dbo.TraceBackOrder(pdaOrderUuid)
WITH (SORT_IN_TEMPDB = ON, ONLINE = OFF);

只有在重复值、字段类型、空间、写入影响和实际执行计划均验收后，才在 888 单独执行。
不得把测试账套结论直接套用到生产账套。
*/
