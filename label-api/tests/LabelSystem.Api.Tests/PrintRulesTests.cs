using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabelSystem.Api;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class PrintRulesTests
{
    private static PrintSnapshot Snapshot(long label=1) => new(label,"888|MAT-01|B01|12.5|000001","000001","12.5","EA",
        "MAT-01","测试物料","B01","WH1","P1","2026-10-19","2027-10-19","{}",1,null,
        7,"transfer","T1","DB001","1",3,"2026-10-19T08:00:00+08:00",new JsonObject());
    private static PrintOutput Output(long line=11,long label=1,long? flow=null)=>new(Guid.NewGuid(),line,"FIRST","READY",flow,Snapshot(label));

    [Fact]
    public void InitialRegistrationSurvivesAttemptAndOutputIdentityChanges()
    {
        var first=PrintRules.Registration("test|888",Output(),"操作员");
        var replay=PrintRules.Registration("test|888",Output(99),"操作员");
        Assert.Equal(first,replay);
        Assert.NotEqual(first.EventKey,PrintRules.Registration("production|008",Output(),"操作员").EventKey);
        Assert.NotEqual(first.EventKey,PrintRules.Registration("test|888",Output(label:2),"操作员").EventKey);
    }
    [Fact]
    public void RegistrationHashMatchesGatewayUtf8LengthPrefixedContract()
    {
        var value=new InitialLabelRegistration(1,"print-initial:test","","888|MAT-01|B01|12.5|000001","000001",12.5m,
            "11111111-1111-4111-8111-111111111111","22222222-2222-4222-8222-222222222222","操作员",
            "MAT-01","测试物料","EA","B01","WH1","P1","2026-10-19","2027-10-19","{}","transfer","T1","DB001","1");
        Assert.Equal("1EB7CFC2390323655BEAAA9A4A4C7984281FDB16A8A3C810ABDD8E686D13D321",PrintRules.RegistrationHash(value));
        Assert.NotEqual(PrintRules.RegistrationHash(value),PrintRules.RegistrationHash(value with{Quantity=12.6m}));
        Assert.NotEqual(PrintRules.RegistrationHash(value),PrintRules.RegistrationHash(value with{TargetLocationCode="P2"}));
    }
    [Theory]
    [InlineData("0")][InlineData("-1")][InlineData("1.0000001")][InlineData("1000000000000")][InlineData("1e2")]
    public void QuantityRejectsInvalidPrecisionAndRange(string value)=>Assert.Throws<ApiException>(()=>PrintRules.ParseQuantity(value));
    [Theory]
    [InlineData("DISPATCH_AUTHORIZED")][InlineData("RECONCILIATION_REQUIRED")][InlineData("TRACEBACK_PENDING")][InlineData("COMPLETED")]
    public void CancellationCannotEraseUnknownOrConfirmedSend(string phase)=>Assert.Throws<ApiException>(()=>PrintRules.RequireKnownUnsent(phase));
    [Fact]
    public void LabelUnlockRequiresEveryOwnOutputAndRegistration()
    {
        var a=Output(label:1,flow:100);var b=Output(label:2);var anotherA=Output(label:1);
        Assert.False(PrintRules.HasRemainingDependencies([a,b],1,false));
        Assert.True(PrintRules.HasRemainingDependencies([a,b],2,false));
        Assert.True(PrintRules.HasRemainingDependencies([a,anotherA],1,false));
        Assert.True(PrintRules.HasRemainingDependencies([a],1,true));
    }
    [Fact]
    public void ReceiptSignatureBindsEntirePayloadAndDevice()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes=Encoding.UTF8.GetBytes("{\"status\":\"submitted\",\"quantity\":\"12.5\"}");
        var signed=new SignedPrintMessage(Convert.ToBase64String(bytes),Convert.ToBase64String(
            key.SignData(bytes,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence)));
        var pub=PrintRules.PublicKey(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        Assert.Equal("submitted",PrintRules.Verify(signed,pub)["status"]!.ToString());
        Assert.Throws<ApiException>(()=>PrintRules.Verify(signed with{Payload=Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"status\":\"submitted\",\"quantity\":\"125\"}"))},pub));
        using var other=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<ApiException>(()=>PrintRules.Verify(signed,Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
    }
    [Theory]
    [InlineData("READY",false,"READY")]
    [InlineData("FAILED_NO_OUTPUT",true,"FAILED_NO_OUTPUT")]
    [InlineData("AUTHORIZED",false,"RECONCILIATION_REQUIRED")]
    [InlineData("UNKNOWN",false,"RECONCILIATION_REQUIRED")]
    public void CompletedAOnlyLeavesBAtItsOwnKnownPhase(string remaining,bool pendingSync,string expected)=>
        Assert.Equal(expected,PrintRules.AggregatePhase([remaining],pendingSync));
    [Fact]
    public void AllLocalOutputsStillWaitForOriginalRegistration()
    {
        Assert.Equal("TRACEBACK_PENDING",PrintRules.AggregatePhase([],true));
        Assert.Equal("COMPLETED",PrintRules.AggregatePhase([],false));
    }
    [Fact]
    public void CanonicalRequestsIgnorePropertyOrderButPreserveArrayOrder()
    {
        var a=JsonNode.Parse("{\"b\":1.0,\"a\":[1,2]}");
        var b=JsonNode.Parse("{\"a\":[1,2],\"b\":1}");
        Assert.Equal(PrintRules.Hash(a),PrintRules.Hash(b));
        Assert.NotEqual(PrintRules.Hash(a),PrintRules.Hash(JsonNode.Parse("{\"a\":[2,1],\"b\":1}")));
    }
    [Theory]
    [InlineData("{\"version\":1,\"stage\":{},\"objects\":[]}")]
    [InlineData("{\"version\":\"wrong\",\"stage\":{},\"objects\":[]}")]
    [InlineData("{\"version\":1,\"stage\":{\"width_mm\":80,\"height_mm\":50},\"objects\":[{\"id\":\"a\",\"type\":\"script\"}]}")]
    public void MalformedTemplateIsClientError(string json)
    {
        var exception=Assert.Throws<ApiException>(()=>PrintRepository.ValidateCanvas(new("模板",JsonDocument.Parse(json).RootElement,80,50,null)));
        Assert.Equal(400,exception.StatusCode);
    }
}
