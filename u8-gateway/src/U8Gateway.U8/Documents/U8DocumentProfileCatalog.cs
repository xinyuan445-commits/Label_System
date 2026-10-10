using System;
using System.Collections.Generic;
using System.Linq;
using U8Gateway.Contracts.Documents;

namespace U8Gateway.U8.Documents
{
    public sealed class U8DocumentProfileCatalog
    {
        private readonly IReadOnlyCollection<U8DocumentProfile> _profiles;

        public U8DocumentProfileCatalog()
        {
            _profiles = new[]
            {
                Profile(DocumentTypeCodes.PurchaseArrival, "VoucherCO_PU", "2", null,
                    "普通采购", "PU_ArrivalVouch", "PU_ArrivalVouchs", null, "captured-read-only",
                    PurchaseOperations("read-runtime-verified-888; test-account-write-validation-required")),
                Profile(DocumentTypeCodes.SubcontractArrival, "VoucherCO_PU", "2", null,
                    "委外加工", "PU_ArrivalVouch", "PU_ArrivalVouchs", null, "captured-read-only",
                    PurchaseOperations("read-schema-captured; implementation-deferred")),
                Profile(DocumentTypeCodes.PurchaseReceipt, "USERPCO", "01", "01",
                    null, "RdRecord01", "RdRecords01", "InvPosition", "captured-read-only",
                    InventoryOperations()),
                Profile(DocumentTypeCodes.SalesDelivery, "VoucherCO_SA", "9", "05",
                    "普通销售", "DispatchList", "DispatchLists", null, "captured-read-only",
                    SalesOperations()),
                Profile(DocumentTypeCodes.SalesOutbound, "USERPCO", "32", "32",
                    null, "RdRecord32", "RdRecords32", "InvPosition", "captured-read-only",
                    InventoryOperations()),
                Profile(DocumentTypeCodes.MaterialOutbound, "USERPCO", "11", "11",
                    null, "RdRecord11", "RdRecords11", "InvPosition", "captured-read-only",
                    InventoryOperations()),
                Profile(DocumentTypeCodes.Transfer, "USERPCO", "12", "12",
                    null, "TransVouch", "TransVouchs", null, "captured-read-only",
                    InventoryOperations()),
                Profile(DocumentTypeCodes.LocationAdjustment, "USERPCO", "19", "19",
                    null, "AdjustPVouch", "AdjustPVouchs", "InvPosition", "captured-read-only",
                    InventoryOperations()),
                Profile(DocumentTypeCodes.InventoryCount, "USERPCO", "18", "18",
                    null, "CheckVouch", "CheckVouchs", "InvPosition", "captured-read-only",
                    InventoryOperations())
            };
        }

        public IReadOnlyCollection<U8DocumentProfile> GetAll()
        {
            return _profiles;
        }

        public U8DocumentProfile Get(string documentType)
        {
            if (string.IsNullOrWhiteSpace(documentType))
            {
                return null;
            }
            return _profiles.FirstOrDefault(x => string.Equals(
                x.DocumentType,
                documentType.Trim(),
                StringComparison.OrdinalIgnoreCase));
        }

        private static U8DocumentProfile Profile(
            string documentType,
            string component,
            string componentDocumentType,
            string databaseVoucherType,
            string businessType,
            string headerTable,
            string lineTable,
            string positionTable,
            string schemaStatus,
            U8OperationBinding operations)
        {
            return new U8DocumentProfile
            {
                DocumentType = documentType,
                Component = component,
                ComponentDocumentType = componentDocumentType,
                DatabaseVoucherType = databaseVoucherType,
                BusinessType = businessType,
                HeaderTable = headerTable,
                LineTable = lineTable,
                PositionTable = positionTable,
                SchemaStatus = schemaStatus,
                Operations = operations
            };
        }

        private static U8OperationBinding InventoryOperations()
        {
            return new U8OperationBinding
            {
                Initialize = "IniLogin",
                Query = "Load",
                Create = "GetNewVouchCode + Insert",
                Update = "Load + Update",
                Verify = "Verify",
                Unverify = "UnVerify",
                Delete = "Load timestamp + Delete",
                Readiness = "read-runtime-verified-888; test-account-write-validation-required"
            };
        }

        private static U8OperationBinding PurchaseOperations(string readiness)
        {
            return new U8OperationBinding
            {
                Initialize = "Init",
                Query = "GetVoucherDataById",
                Create = "GetVoucherNO + VoucherSave",
                Update = "Modify",
                Verify = "ConfirmArr",
                Unverify = "CancelconfirmArr",
                Delete = "Delete",
                Readiness = readiness
            };
        }

        private static U8OperationBinding SalesOperations()
        {
            return new U8OperationBinding
            {
                Initialize = "USSAServer.clsSystem.Init; VoucherCO_SA InitSys + Init for writes",
                Query = "USSAServer.clsVouchLoad.GetVoucherDataWEB",
                Create = "GetVoucherNO + Save",
                Update = "Modify",
                Verify = "VerifyVouch(true)",
                Unverify = "VerifyVouch(false)",
                Delete = "Delete",
                Readiness = "read-runtime-verified-888; test-account-write-validation-required"
            };
        }
    }
}
