using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabelSystem.Api;
using Xunit;

public class PrintSourceTests
{
    private const string Key="development-test-key-never-used-by-deployment";
    private static VerifiedPrintSource Source()=>new(Guid.NewGuid().ToString(),new string('A',64),"user",1100,
        [new(new string('B',64),"BILL","CGRK:1","B1","1","M","U","B","W","L",100,0,[],[60,40])]);
    private static SignedPrintMessage Sign(VerifiedPrintSource source)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(source,PrintRules.Json);
        return new(Convert.ToBase64String(bytes),Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Key),bytes)));
    }
    [Fact] public void ValidVerifiedSourceIsAccepted()=>Assert.Equal(2,PrintSourceVerifier.Verify(Sign(Source()),Key,"user",1000).Rows[0].Amounts.Count);
    [Fact] public void ChangedBodyOrUserCannotReserve()
    {
        var signed=Sign(Source());
        Assert.Throws<ApiException>(()=>PrintSourceVerifier.Verify(signed with{Payload=Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))},Key,"user",1000));
        Assert.Throws<ApiException>(()=>PrintSourceVerifier.Verify(signed,Key,"other",1000));
    }
    [Fact] public void ExpiredAndInvalidSourcesFailClosed()
    {
        var source=Source();
        Assert.Throws<ApiException>(()=>PrintSourceVerifier.Verify(Sign(source),Key,"user",1200));
        Assert.Throws<ApiException>(()=>PrintSourceVerifier.Verify(Sign(source with{PlanHash="invalid"}),Key,"user",1000));
        Assert.Throws<ApiException>(()=>PrintSourceVerifier.Verify(Sign(source with{Rows=[source.Rows[0] with{Amounts=[-1]}]}),Key,"user",1000));
        Assert.Throws<ApiException>(()=>PrintSourceVerifier.Verify(Sign(source with{Rows=[source.Rows[0],source.Rows[0]]}),Key,"user",1000));
    }
}
