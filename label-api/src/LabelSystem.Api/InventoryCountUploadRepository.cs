using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

public sealed record InventoryCountContribution(string Code, string MaterialCode, string? BatchNo,
    string WarehouseCode, string? LocationCode, decimal ActualQuantity);

// Freeze the previous contributions once. A retry must send the identical Gateway payload,
// including after the native save succeeded but its response was lost.
public sealed class InventoryCountUploadRepository(SqlConnectionFactory connections)
{
    public async Task<InventoryCountContribution[]> GetOrCreateAsync(MaterialOutboundExecutionOperation operation,
        InventoryCountUploadContext context, CancellationToken ct)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var gate = new SqlCommand("""
            DECLARE @r int;
            EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @r<0 THROW 51000,N'盘点上传正忙，请稍后重试。',1;
            SELECT previous_labels_json FROM dbo.inventory_count_upload_plan WHERE operation_id=@operation;
            """, connection, transaction);
        gate.Parameters.AddWithValue("@resource", "count-upload:" + (context.Source?.Document.Id.ToString() ?? "new:" + operation.Id));
        gate.Parameters.AddWithValue("@operation", operation.Id);
        if (await gate.ExecuteScalarAsync(ct) is string saved)
        {
            await transaction.CommitAsync(ct);
            return JsonSerializer.Deserialize<InventoryCountContribution[]>(saved)!;
        }
        var previous = new List<InventoryCountContribution>();
        if (context.Source is { } source)
        {
            await using var busy = new SqlCommand("""
                SELECT COUNT_BIG(*) FROM dbo.inventory_count_upload_plan p
                JOIN dbo.business_operation o ON o.id=p.operation_id
                WHERE (p.source_document_id=@source OR p.source_document_id IS NULL) AND o.id<>@operation
                  AND o.status IN ('PENDING','WRITING_ERP','NEEDS_RECONCILIATION','PENDING_APPROVAL');
                """, connection, transaction);
            busy.Parameters.AddWithValue("@source", source.Document.Id);
            busy.Parameters.AddWithValue("@operation", operation.Id);
            if (Convert.ToInt64(await busy.ExecuteScalarAsync(ct)) != 0)
                throw new ApiException(409, "COUNT_UPLOAD_BUSY", "有盘点单尚未完成上传，请先处理。");
            await using var history = new SqlCommand("""
                WITH observations AS (
                    SELECT p.label_code,l.material_code,l.batch_no,l.from_warehouse_code,l.from_location_code,l.requested_qty,
                           ROW_NUMBER() OVER(PARTITION BY p.label_code ORDER BY o.id DESC,l.id DESC) AS rn
                    FROM dbo.business_operation o
                    JOIN dbo.business_operation_line l ON l.operation_id=o.id
                    JOIN dbo.product_label p ON p.id=l.label_id
                    WHERE o.erp_document_type='inventory-count' AND o.erp_status='SUCCESS' AND o.status='SUCCESS'
                      AND o.erp_document_id=@source AND o.erp_account_id=@account AND o.erp_database_name_snapshot=@database
                      AND o.id<>@operation
                )
                SELECT label_code,material_code,batch_no,from_warehouse_code,from_location_code,requested_qty
                FROM observations WHERE rn=1 ORDER BY label_code;
                """, connection, transaction);
            history.Parameters.AddWithValue("@source", source.Document.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            history.Parameters.AddWithValue("@account", context.AccountId);
            history.Parameters.AddWithValue("@database", context.DatabaseName);
            history.Parameters.AddWithValue("@operation", operation.Id);
            await using var reader = await history.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                previous.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetDecimal(5)));
        }
        var contributions = InventoryCountUploadRules.PreviousForUpload(previous, operation.Lines, context.CountByLocation);
        await using var insert = new SqlCommand("""
            INSERT dbo.inventory_count_upload_plan(operation_id,source_document_id,previous_labels_json)
            VALUES(@operation,@source,@json);
            """, connection, transaction);
        insert.Parameters.AddWithValue("@operation", operation.Id);
        insert.Parameters.AddWithValue("@source", (object?)context.Source?.Document.Id ?? DBNull.Value);
        insert.Parameters.AddWithValue("@json", JsonSerializer.Serialize(contributions));
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return contributions;
    }
}
