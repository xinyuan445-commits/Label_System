using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabelSystem.Api;

public static class PrintSourceVerifier
{
    public static VerifiedPrintSource Verify(SignedPrintMessage envelope,string key,string userId,long now)
    {
        if(key.Length<32)throw new ApiException(503,"PRINT_SOURCE_DISABLED","单据打印来源校验尚未配置。");
        try
        {
            var bytes=Convert.FromBase64String(envelope.Payload);
            if(bytes.Length>1024*1024)throw new FormatException();
            var expected=HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),bytes);
            if(!CryptographicOperations.FixedTimeEquals(expected,Convert.FromHexString(envelope.Signature)))throw new FormatException();
            var source=JsonSerializer.Deserialize<VerifiedPrintSource>(bytes,PrintRules.Json)??throw new FormatException();
            if(source.UserId!=userId || source.ExpiresAt<now || source.ExpiresAt>now+300 ||
                !Hex(source.PlanHash) || source.Rows is null ||
                source.Rows.Count is <1 or >1000 || source.Rows.Sum(r=>r.Amounts.Count)>1000 ||
                source.Rows.Select(r=>r.SourceKey).Distinct(StringComparer.Ordinal).Count()!=source.Rows.Count ||
                source.Rows.Any(r=>!Hex(r.SourceKey) || r.SourceKind is not ("BILL" or "STOCK") ||
                  !Text(r.MaterialCode,100)||!Text(r.UnitCode,50)||!Text(r.DocumentId,100)||
                  !Text(r.DocumentNo,100)||!Text(r.DetailId,100)||r.BatchNo is null||r.BatchNo.Length>100||
                  r.LocationCode is null||r.LocationCode.Length>50||
                  !Text(r.WarehouseCode,50)||r.EligibleQuantity<0||r.LegacyQuantity<0||
                  r.Amounts.Count==0||r.Amounts.Any(q=>q<=0||decimal.Round(q,6)!=q)||r.LegacyCodes.Count>10000))throw new FormatException();
            _=PrintRules.RequestId(source.ClientRequestId);
            return source;
        }
        catch(Exception ex) when(ex is FormatException or JsonException or ArgumentException or NullReferenceException or OverflowException)
        {throw new ApiException(409,"PRINT_SOURCE_INVALID","单据或库存已变化，请重新查询。");}
    }
    private static bool Hex(string? value)=>value is {Length:64}&&value.All(c=>char.IsAsciiHexDigit(c));
    private static bool Text(string? value,int max)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max;
}
