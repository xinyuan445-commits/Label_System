using LabelSystem.Api;
using Xunit;

public class PrintSimulationTests
{
    private static PrintingOptions Printing()=>new(){SimulationOnly=true,SimulationDeviceId=Guid.NewGuid(),
        SimulationDeviceKeyHash=new string('A',64),SimulationStatePath=Path.GetFullPath("simulation-test")};
    private static LabelSystemOptions Labels()=>new(){Environment="development",ErpAccountId="888",ErpDatabaseName="UFDATA_888_2021"};
    [Fact]
    public void ExactDevelopment888BoundaryIsAccepted()=>PrintSimulationBoundary.Validate(Printing(),Labels(),"LabelSystem_Dev");
    [Theory]
    [InlineData("production","888","UFDATA_888_2021","LabelSystem_Dev")]
    [InlineData("development","008","UFDATA_888_2021","LabelSystem_Dev")]
    [InlineData("development","888","UFDATA_008_2021","LabelSystem_Dev")]
    [InlineData("development","888","UFDATA_888_2021","LabelSystem_Prod")]
    public void OtherEnvironmentsCannotEnableSimulation(string env,string account,string erp,string db)
    {
        var labels=Labels();labels.Environment=env;labels.ErpAccountId=account;labels.ErpDatabaseName=erp;
        Assert.Throws<ApiException>(()=>PrintSimulationBoundary.Validate(Printing(),labels,db));
    }
    [Fact]
    public void MissingPinnedDeviceCannotEnableSimulation()
    {
        var p=Printing();p.SimulationDeviceId=Guid.Empty;
        Assert.Throws<ApiException>(()=>PrintSimulationBoundary.Validate(p,Labels(),"LabelSystem_Dev"));
        p=Printing();p.SimulationDeviceKeyHash="invalid";
        Assert.Throws<ApiException>(()=>PrintSimulationBoundary.Validate(p,Labels(),"LabelSystem_Dev"));
    }
}
