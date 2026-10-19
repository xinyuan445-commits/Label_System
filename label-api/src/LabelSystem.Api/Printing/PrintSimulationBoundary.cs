namespace LabelSystem.Api;

public static class PrintSimulationBoundary
{
    public static void Validate(PrintingOptions printing, LabelSystemOptions labels, string database)
    {
        if (!printing.SimulationOnly) return;
        if (labels.Environment != "development" || labels.ErpAccountId != "888" ||
            labels.ErpDatabaseName != "UFDATA_888_2021" || database != "LabelSystem_Dev" ||
            printing.SimulationDeviceId == Guid.Empty || printing.SimulationDeviceKeyHash.Length != 64 ||
            printing.SimulationDeviceKeyHash.Any(c => !Uri.IsHexDigit(c)) ||
            !Path.IsPathFullyQualified(printing.SimulationStatePath))
            throw new ApiException(503,"PRINT_SIMULATION_CONFIGURATION_INVALID","888模拟环境配置无效。");
    }
}
