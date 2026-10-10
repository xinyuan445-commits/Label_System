using System.Collections.Generic;

namespace U8Gateway.Core.Documents
{
    public sealed class DocumentOperationRequest
    {
        public string ClientRequestId { get; set; }
        public string OperatorId { get; set; }
        public string OperatorName { get; set; }
        public string DeviceId { get; set; }
        public string DeviceModel { get; set; }
        public string ApkVersion { get; set; }
        public string H5Version { get; set; }
        public string BridgeVersion { get; set; }
        public string Reason { get; set; }
        public string ExpectedVersion { get; set; }
        public IDictionary<string, object> Header { get; set; }
        public IList<IDictionary<string, object>> Lines { get; set; }
        public IList<IDictionary<string, object>> Positions { get; set; }
        public IDictionary<string, IList<IDictionary<string, object>>> Auxiliary { get; set; }
        public IDictionary<string, object> Options { get; set; }
    }

    public sealed class DocumentPayload
    {
        public IDictionary<string, object> Header { get; set; }
        public IList<IDictionary<string, object>> Lines { get; set; }
        public IList<IDictionary<string, object>> Positions { get; set; }
        public IDictionary<string, IList<IDictionary<string, object>>> Auxiliary { get; set; }
    }

    public sealed class DocumentOperationResult
    {
        public bool Confirmed { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string VoucherId { get; set; }
        public string VoucherCode { get; set; }
        public string Version { get; set; }
        public object Document { get; set; }
    }
}
