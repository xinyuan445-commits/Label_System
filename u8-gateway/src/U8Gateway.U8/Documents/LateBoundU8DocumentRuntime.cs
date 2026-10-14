using System;
using System.Data.SqlClient;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CSharp.RuntimeBinder;
using ADODB;
using Info_PU;
using MSXML2;
using U8Gateway.Contracts.Documents;
using U8Gateway.Contracts.Metadata;
using U8Gateway.Core.Documents;
using U8Gateway.Core.Metadata;
using U8Gateway.Core.U8;
using VoucherCO_PU;
using VoucherCO_SA;
using VoucherVerify;
using U8Gateway.Contracts;
using U8Gateway.UserpcoBridge;

namespace U8Gateway.U8.Documents
{
    public sealed class LateBoundU8DocumentRuntime : IU8DocumentRuntime
    {
        private readonly IU8OperationQueue _queue;
        private readonly Func<U8GatewayRuntimeOptions> _options;
        private readonly IDocumentDefinitionStore _definitions;
        private readonly IU8XmlCodec _codec;

        public LateBoundU8DocumentRuntime(
            IU8OperationQueue queue,
            Func<U8GatewayRuntimeOptions> options,
            IDocumentDefinitionStore definitions,
            IU8XmlCodec codec)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }

        public Task<DocumentOperationResult> ExecuteAsync(
            U8DocumentExecution execution,
            CancellationToken cancellationToken)
        {
            if (execution == null) throw new ArgumentNullException(nameof(execution));
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<DocumentOperationResult>(cancellationToken);
            }
            if (string.Equals(
                execution.Operation,
                DocumentOperationCodes.Query,
                StringComparison.OrdinalIgnoreCase))
            {
                return _queue.ExecuteAsync(() => Query(execution));
            }
            return _queue.ExecuteAsync(() => Write(execution));
        }

        private DocumentOperationResult Write(U8DocumentExecution execution)
        {
            U8GatewayRuntimeOptions options = _options();
            ValidateWriteOptions(options);
            if (string.Equals(execution.Operation, DocumentOperationCodes.Add,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(execution.Profile.Component, "USERPCO",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CreateInventory(options, execution);
            }
            if (string.Equals(execution.Operation, DocumentOperationCodes.Add,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(execution.Profile.Component, "VoucherCO_PU",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CreatePurchase(options, execution);
            }
            if (string.Equals(execution.Operation, DocumentOperationCodes.Add,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(execution.Profile.Component, "VoucherCO_SA",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CreateSales(options, execution);
            }
            if (string.Equals(execution.Operation, DocumentOperationCodes.Validate,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(execution.Operation, DocumentOperationCodes.Edit,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Disabled(execution.VoucherCode);
            }
            if (!string.Equals(execution.Operation, DocumentOperationCodes.Verify,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(execution.Operation, DocumentOperationCodes.Unverify,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(execution.Operation, DocumentOperationCodes.Delete,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    "U8_OPERATION_UNSUPPORTED",
                    "不支持该操作。",
                    execution.VoucherCode);
            }
            return ChangeState(options, execution);
        }

        private DocumentOperationResult CreateInventory(
            U8GatewayRuntimeOptions options,
            U8DocumentExecution execution)
        {
            DocumentDefinition definition = _definitions.Get(execution.Profile.DocumentType);
            if (definition == null)
            {
                return Failure("DOCUMENT_TYPE_NOT_FOUND", "单据类型不存在。", null);
            }
            if (execution.Request == null)
            {
                return Failure("REQUEST_REQUIRED", "请求体不能为空。", null);
            }

            var payload = new DocumentPayload
            {
                Header = execution.Request.Header,
                Lines = execution.Request.Lines,
                Positions = execution.Request.Positions,
                Auxiliary = execution.Request.Auxiliary
            };
            U8XmlPacket packet = _codec.Encode(definition, payload);
            U8Login.clsLogin login = null;
            object voucher = null;
            IXMLDOMDocument2 head = null;
            IXMLDOMDocument2 body = null;
            IXMLDOMDocument2 position = null;
            string voucherCode = null;
            string voucherId = string.Empty;
            try
            {
                login = Login(options.U8);
                voucher = Create("USERPCO.VoucherCO");
                dynamic native = voucher;
                string error = string.Empty;
                if (!native.IniLogin(login, ref error))
                {
                    throw new InvalidOperationException(Empty(error, "USERPCO 初始化失败。"));
                }

                string numberError = string.Empty;
                string[] numberTypes = new[]
                {
                    execution.Profile.ComponentDocumentType,
                    definition.U8?.NumberType
                }.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (string numberType in numberTypes)
                {
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        IXMLDOMDocument2 numberHead = null;
                        try
                        {
                            numberHead = ParseXml(packet.HeaderXml, "自动取号表头");
                            string attemptError = string.Empty;
                            bool validate = false;
                            bool phase = false;
                            try
                            {
                                voucherCode = native.GetNewVouchCode(
                                    numberType,
                                    numberHead,
                                    ref attemptError,
                                    ref validate,
                                    ref phase);
                            }
                            catch (COMException exception)
                            {
                                attemptError = "COM HRESULT=0x" +
                                    exception.ErrorCode.ToString("X8") + "，" +
                                    exception.Message;
                            }
                            if (!string.IsNullOrWhiteSpace(voucherCode)) break;
                            numberError = attemptError;
                        }
                        finally
                        {
                            Release(numberHead);
                        }
                        if (attempt == 0) Thread.Sleep(300);
                    }
                    if (!string.IsNullOrWhiteSpace(voucherCode)) break;
                }
                if (string.IsNullOrWhiteSpace(voucherCode))
                {
                    return Failure(
                        "U8_VOUCHER_NUMBER_FAILED",
                        Empty(numberError, "U8 未生成单据号。"),
                        null);
                }

                head = ParseXml(
                    SetFirstRowAttribute(
                        packet.HeaderXml,
                        CodeColumn(execution.Profile.DocumentType),
                        voucherCode.Trim()),
                    "表头");
                body = ParseXml(packet.BodyXml, "表体");
                position = new DOMDocument26Class();
                error = string.Empty;

                bool inserted;
                try
                {
                    bool redVoucher = OptionBoolean(execution.Request.Options, "redVoucher");
                    UserpcoInsertResult insert = UserpcoWriter.Insert(
                        voucher,
                        execution.Profile.ComponentDocumentType,
                        head,
                        body,
                        position,
                        redVoucher);
                    inserted = insert.Succeeded;
                    error = insert.Error;
                    voucherId = insert.VoucherId;
                }
                catch (RuntimeBinderException exception)
                {
                    return Failure(
                        "U8_CALL_BINDING_FAILED",
                        exception.Message,
                        voucherCode);
                }
                catch (ArgumentException exception)
                {
                    return Failure(
                        "U8_CALL_BINDING_FAILED",
                        exception.Message,
                        voucherCode);
                }
                catch (System.Reflection.TargetInvocationException exception)
                {
                    inserted = false;
                    error = exception.InnerException?.Message ?? exception.Message;
                }
                catch (COMException exception)
                {
                    inserted = false;
                    error = exception.Message;
                }

                long returnedId;
                long? confirmedId = long.TryParse(voucherId, out returnedId) &&
                                    ReadVoucherState(options, execution.Profile, returnedId).Exists
                    ? returnedId
                    : (long?)null;
                if (!confirmedId.HasValue)
                {
                    confirmedId = WaitForVoucherId(
                        options, execution.Profile, voucherCode);
                }
                if (!inserted && !confirmedId.HasValue)
                {
                    return Failure(
                        "U8_CREATE_REJECTED",
                        Empty(error, "U8 新增失败。"),
                        voucherCode);
                }
                if (!confirmedId.HasValue)
                {
                    throw new InvalidOperationException(
                        Empty(error, "U8 返回新增成功，但回查不到单据。") +
                        " 返回ID=" + Empty(voucherId, "空") +
                        "，预取单号=" + Empty(voucherCode, "空") + "。");
                }

                VoucherState state = ReadVoucherState(
                    options, execution.Profile, confirmedId.Value);
                U8XmlPacket saved = QueryInventory(
                    options.U8, execution.Profile, confirmedId.Value);
                return new DocumentOperationResult
                {
                    Confirmed = state.Exists,
                    OutcomeKnown = state.Exists,
                    Code = state.Exists ? "OK" : "U8_WRITE_NOT_CONFIRMED",
                    Message = state.Exists ? Empty(error, "成功") : "U8 新增后回查未确认。",
                    VoucherId = confirmedId.Value.ToString(),
                    VoucherCode = voucherCode,
                    Version = state.Version,
                    Document = _codec.Decode(definition, saved)
                };
            }
            finally
            {
                Shutdown(login);
                Release(position);
                Release(body);
                Release(head);
                Release(voucher);
                Release(login);
            }
        }

        private DocumentOperationResult CreatePurchase(
            U8GatewayRuntimeOptions options,
            U8DocumentExecution execution)
        {
            DocumentDefinition definition = _definitions.Get(execution.Profile.DocumentType);
            if (definition == null) return Failure("DOCUMENT_TYPE_NOT_FOUND", "单据类型不存在。", null);
            if (execution.Request == null) return Failure("REQUEST_REQUIRED", "请求体不能为空。", null);

            U8XmlPacket packet = _codec.Encode(definition, new DocumentPayload
            {
                Header = execution.Request.Header,
                Lines = execution.Request.Lines,
                Positions = execution.Request.Positions,
                Auxiliary = execution.Request.Auxiliary
            });
            U8Login.clsLogin login = null;
            object common = null;
            Connection connection = null;
            clsVoucherCO_PU voucher = null;
            IXMLDOMDocument2 head = null;
            IXMLDOMDocument2 body = null;
            IXMLDOMDocument2 format = null;
            IXMLDOMDocument2 current = null;
            IXMLDOMDocument2 message = null;
            string voucherCode = string.Empty;
            string saveMessage = string.Empty;
            try
            {
                head = ParseXml(packet.HeaderXml, "采购单表头");
                body = ParseXml(packet.BodyXml, "采购单表体");
                login = Login(options.U8);
                common = Create("USCOMMON.Login");
                dynamic commonLogin = common;
                U8Login.clsLogin source = login;
                commonLogin.Load(ref source);
                connection = ConvertConnection(commonLogin.AccountConnection);

                voucher = new clsVoucherCO_PUClass();
                ClsS_Infor information = null;
                bool positive = !OptionBoolean(execution.Request.Options, "redVoucher");
                string billType = string.Empty;
                string businessType = execution.Profile.BusinessType ?? string.Empty;
                UseMode useMode = UseMode.CS;
                voucher.Init(
                    (vouchertype)2,
                    ref login,
                    ref connection,
                    ref information,
                    ref positive,
                    ref billType,
                    ref businessType,
                    ref useMode,
                    businessType,
                    execution.Request.Header != null &&
                    execution.Request.Header.TryGetValue("cptcode", out object purchaseType)
                        ? Convert.ToString(purchaseType)
                        : string.Empty);

                string voucherType = execution.Profile.ComponentDocumentType;
                string error = string.Empty;
                bool getFormatOnly = false;
                bool selfFormat = false;
                bool getRealNo = true;
                bool synchronization = false;
                bool numbered = voucher.GetVoucherNO(
                    ref head,
                    ref voucherType,
                    ref error,
                    ref voucherCode,
                    ref format,
                    ref getFormatOnly,
                    ref selfFormat,
                    ref getRealNo,
                    ref synchronization);
                if (!numbered || string.IsNullOrWhiteSpace(voucherCode))
                    return Failure("U8_CREATE_REJECTED", Empty(error, "U8 未生成单据号。"), voucherCode);

                object currentId = null;
                string overDetails = string.Empty;
                message = new DOMDocument26Class();
                saveMessage = voucher.VoucherSave(
                    head, body, 0, ref currentId, ref current, UseMode.CS,
                    ref overDetails, ref message);

                long? confirmedId = WaitForVoucherId(options, execution.Profile, voucherCode);
                if (!confirmedId.HasValue)
                    return Failure("U8_WRITE_NOT_CONFIRMED",
                        Empty(saveMessage, MessageText(message, "U8 新增后回查未确认。")),
                        voucherCode, false);
                VoucherState state = ReadVoucherState(options, execution.Profile, confirmedId.Value);
                U8XmlPacket saved = QueryPurchase(options.U8, execution.Profile, confirmedId.Value);
                return new DocumentOperationResult
                {
                    Confirmed = state.Exists,
                    OutcomeKnown = state.Exists,
                    Code = state.Exists ? "OK" : "U8_WRITE_NOT_CONFIRMED",
                    Message = state.Exists ? Empty(saveMessage, "成功") : "U8 新增后回查未确认。",
                    VoucherId = confirmedId.Value.ToString(),
                    VoucherCode = voucherCode,
                    Version = state.Version,
                    Document = _codec.Decode(definition, saved)
                };
            }
            finally
            {
                Shutdown(login);
                Release(message);
                Release(current);
                Release(format);
                Release(body);
                Release(head);
                Release(voucher);
                Release(connection);
                Release(common);
                Release(login);
            }
        }

        private DocumentOperationResult CreateSales(
            U8GatewayRuntimeOptions options,
            U8DocumentExecution execution)
        {
            DocumentDefinition definition = _definitions.Get(execution.Profile.DocumentType);
            if (definition == null) return Failure("DOCUMENT_TYPE_NOT_FOUND", "单据类型不存在。", null);
            if (execution.Request == null) return Failure("REQUEST_REQUIRED", "请求体不能为空。", null);

            U8XmlPacket packet = _codec.Encode(definition, new DocumentPayload
            {
                Header = execution.Request.Header,
                Lines = execution.Request.Lines,
                Positions = execution.Request.Positions,
                Auxiliary = execution.Request.Auxiliary
            });
            U8Login.clsLogin login = null;
            object common = null;
            Connection connection = null;
            ClsVoucherCO_SA voucher = null;
            IXMLDOMDocument2 head = null;
            IXMLDOMDocument2 body = null;
            IXMLDOMDocument2 format = null;
            IXMLDOMDocument2 config = null;
            object system = null;
            string voucherCode = string.Empty;
            string saveMessage = string.Empty;
            try
            {
                head = ParseXml(packet.HeaderXml, "销售单表头");
                body = ParseXml(packet.BodyXml, "销售单表体");
                login = Login(options.U8);
                common = Create("USCOMMON.Login");
                dynamic commonLogin = common;
                U8Login.clsLogin source = login;
                commonLogin.Load(ref source);
                connection = ConvertConnection(commonLogin.AccountConnection);

                bool red = OptionBoolean(execution.Request.Options, "redVoucher");
                VoucherTypeSA voucherType = red ? VoucherTypeSA.DispatchRed : VoucherTypeSA.DispatchBlue;
                voucher = new ClsVoucherCO_SAClass();
                if (!voucher.InitSys(ref login, voucherType, ref system))
                    throw new InvalidOperationException("销售 CO 初始化失败。");
                string userMode = "CS";
                voucher.Init(voucherType, ref login, ref connection, ref userMode, ref system);

                string error = string.Empty;
                bool getFormatOnly = false;
                bool selfFormat = false;
                bool numbered = voucher.GetVoucherNO(
                    ref head, ref voucherCode, ref error, ref format,
                    ref getFormatOnly, ref selfFormat);
                if (!numbered || string.IsNullOrWhiteSpace(voucherCode))
                    return Failure("U8_CREATE_REJECTED", Empty(error, "U8 未生成单据号。"), voucherCode);

                object currentId = null;
                config = new DOMDocument26Class();
                saveMessage = voucher.Save(head, body, 0, ref currentId, ref config);
                long? confirmedId = WaitForVoucherId(options, execution.Profile, voucherCode);
                if (!confirmedId.HasValue)
                    return Failure("U8_WRITE_NOT_CONFIRMED",
                        Empty(saveMessage, "U8 新增后回查未确认。"), voucherCode, false);
                VoucherState state = ReadVoucherState(options, execution.Profile, confirmedId.Value);
                U8XmlPacket saved = QuerySales(options.U8, confirmedId.Value);
                return new DocumentOperationResult
                {
                    Confirmed = state.Exists,
                    OutcomeKnown = state.Exists,
                    Code = state.Exists ? "OK" : "U8_WRITE_NOT_CONFIRMED",
                    Message = state.Exists ? Empty(saveMessage, "成功") : "U8 新增后回查未确认。",
                    VoucherId = confirmedId.Value.ToString(),
                    VoucherCode = voucherCode,
                    Version = state.Version,
                    Document = _codec.Decode(definition, saved)
                };
            }
            finally
            {
                Shutdown(login);
                Release(config);
                Release(format);
                Release(system);
                Release(body);
                Release(head);
                Release(voucher);
                Release(connection);
                Release(common);
                Release(login);
            }
        }

        private DocumentOperationResult Query(U8DocumentExecution execution)
        {
            U8GatewayRuntimeOptions options = _options();
            ValidateOptions(options);
            DocumentDefinition definition = _definitions.Get(
                execution.Profile.DocumentType);
            if (definition == null)
            {
                return Failure("DOCUMENT_TYPE_NOT_FOUND", "单据类型不存在。", execution.VoucherCode);
            }

            long voucherId = ResolveVoucherId(
                options,
                execution.Profile,
                execution.VoucherCode);
            U8XmlPacket packet;
            if (string.Equals(execution.Profile.Component, "USERPCO", StringComparison.OrdinalIgnoreCase))
            {
                packet = QueryInventory(options.U8, execution.Profile, voucherId);
            }
            else if (string.Equals(execution.Profile.Component, "VoucherCO_PU", StringComparison.OrdinalIgnoreCase))
            {
                packet = QueryPurchase(options.U8, execution.Profile, voucherId);
            }
            else if (string.Equals(execution.Profile.Component, "VoucherCO_SA", StringComparison.OrdinalIgnoreCase))
            {
                packet = QuerySales(options.U8, voucherId);
            }
            else
            {
                return Failure("U8_COMPONENT_UNSUPPORTED", "U8 单据组件不受支持。", execution.VoucherCode);
            }

            DocumentPayload document = _codec.Decode(definition, packet);
            VoucherState state = ReadVoucherState(options, execution.Profile, voucherId);
            return new DocumentOperationResult
            {
                Confirmed = true,
                OutcomeKnown = true,
                Code = "OK",
                Message = "成功",
                VoucherId = voucherId.ToString(),
                VoucherCode = execution.VoucherCode,
                Version = state.Version,
                Document = document
            };
        }

        private DocumentOperationResult ChangeState(
            U8GatewayRuntimeOptions options,
            U8DocumentExecution execution)
        {
            long? voucherId = TryResolveVoucherId(
                options,
                execution.Profile,
                execution.VoucherCode);
            bool deleting = string.Equals(
                execution.Operation,
                DocumentOperationCodes.Delete,
                StringComparison.OrdinalIgnoreCase);
            if (!voucherId.HasValue)
            {
                return deleting
                    ? Success(execution, null, null, "单据已不存在。")
                    : Failure(
                        "VOUCHER_NOT_FOUND",
                        "单据不存在。",
                        execution.VoucherCode);
            }

            VoucherState before = ReadVoucherState(
                options,
                execution.Profile,
                voucherId.Value);
            string expectedVersion = execution.Request?.ExpectedVersion;
            if (!string.IsNullOrWhiteSpace(expectedVersion) &&
                !string.Equals(
                    expectedVersion.Trim(),
                    before.Version,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    "VERSION_CONFLICT",
                    "单据已发生变化，请重新查询。",
                    execution.VoucherCode);
            }

            bool verifying = string.Equals(
                execution.Operation,
                DocumentOperationCodes.Verify,
                StringComparison.OrdinalIgnoreCase);
            bool unverifying = string.Equals(
                execution.Operation,
                DocumentOperationCodes.Unverify,
                StringComparison.OrdinalIgnoreCase);
            if ((verifying && before.Verified) || (unverifying && !before.Verified))
            {
                return Success(
                    execution,
                    voucherId,
                    before.Version,
                    "单据已处于目标状态。");
            }

            string nativeMessage = null;
            bool nativeRejected = false;
            Exception nativeException = null;
            try
            {
                if (string.Equals(execution.Profile.Component, "USERPCO",
                    StringComparison.OrdinalIgnoreCase))
                {
                    NativeStateChangeResult native = ChangeInventoryState(
                        options.U8,
                        execution.Profile,
                        voucherId.Value,
                        execution.Operation);
                    nativeMessage = native.Message;
                    nativeRejected = !native.Succeeded;
                }
                else if (string.Equals(execution.Profile.Component, "VoucherCO_PU",
                    StringComparison.OrdinalIgnoreCase))
                {
                    nativeMessage = ChangePurchaseState(
                        options.U8,
                        execution.Profile,
                        voucherId.Value,
                        execution.Operation);
                }
                else if (string.Equals(execution.Profile.Component, "VoucherCO_SA",
                    StringComparison.OrdinalIgnoreCase))
                {
                    nativeMessage = ChangeSalesState(
                        options.U8,
                        voucherId.Value,
                        execution.Operation);
                }
                else
                {
                    return Failure(
                        "U8_COMPONENT_UNSUPPORTED",
                        "U8 单据组件不受支持。",
                        execution.VoucherCode);
                }
            }
            catch (Exception exception)
            {
                nativeException = exception;
            }

            VoucherState after = WaitForState(
                options,
                execution.Profile,
                voucherId.Value,
                deleting,
                verifying);
            bool confirmed = deleting
                ? !after.Exists
                : after.Exists && after.Verified == verifying;
            if (nativeException == null && confirmed)
            {
                return Success(
                    execution,
                    voucherId,
                    after.Version,
                    string.IsNullOrWhiteSpace(nativeMessage)
                        ? "成功"
                        : nativeMessage);
            }
            if (nativeException != null)
            {
                string detail = InnermostMessage(nativeException);
                throw new InvalidOperationException(
                    confirmed
                        ? "U8 状态已变化，但 CO 返回异常，需要人工核对：" + detail
                        : "U8 操作返回异常且回查未确认：" + detail,
                    nativeException);
            }
            if (nativeRejected)
            {
                return Failure(
                    verifying ? "U8_VERIFY_REJECTED" :
                    unverifying ? "U8_UNVERIFY_REJECTED" : "U8_DELETE_REJECTED",
                    Empty(nativeMessage, "U8 拒绝执行该操作。"),
                    execution.VoucherCode);
            }
            return Failure(
                "U8_WRITE_NOT_CONFIRMED",
                Empty(nativeMessage, "U8 操作后回查未确认。"),
                execution.VoucherCode,
                false);
        }

        private static NativeStateChangeResult ChangeInventoryState(
            U8ConnectionOptions options,
            U8DocumentProfile profile,
            long voucherId,
            string operation)
        {
            U8Login.clsLogin login = null;
            object voucher = null;
            object head = null;
            object body = null;
            object position = null;
            Connection connection = null;
            try
            {
                login = Login(options);
                voucher = Create("USERPCO.VoucherCO");
                dynamic native = voucher;
                string error = string.Empty;
                if (!native.IniLogin(login, ref error))
                {
                    return NativeStateChangeResult.Rejected(
                        Empty(error, "USERPCO 初始化失败。"));
                }
                object id = checked((int)voucherId);
                head = Create("MSXML2.DOMDocument.3.0");
                body = Create("MSXML2.DOMDocument.3.0");
                position = Create("MSXML2.DOMDocument.3.0");
                bool loaded = native.Load(
                    profile.ComponentDocumentType,
                    "ID=" + voucherId,
                    ref head,
                    ref body,
                    ref position,
                    ref error,
                    false,
                    string.Empty);
                if (!loaded)
                {
                    return NativeStateChangeResult.Rejected(
                        Empty(error, "操作前加载 U8 单据失败。"));
                }
                object timestamp = FirstAttribute(Xml(head), "ufts");
                if (timestamp == null)
                {
                    return NativeStateChangeResult.Rejected("U8 单据缺少并发时间戳。");
                }
                bool succeeded;
                if (string.Equals(operation, DocumentOperationCodes.Verify,
                    StringComparison.OrdinalIgnoreCase))
                {
                    UserpcoStateResult result = UserpcoWriter.ChangeState(
                        voucher,
                        profile.ComponentDocumentType,
                        id,
                        timestamp,
                        true);
                    return new NativeStateChangeResult(result.Succeeded, result.Error);
                }
                else if (string.Equals(operation, DocumentOperationCodes.Unverify,
                    StringComparison.OrdinalIgnoreCase))
                {
                    UserpcoStateResult result = UserpcoWriter.ChangeState(
                        voucher,
                        profile.ComponentDocumentType,
                        id,
                        timestamp,
                        false);
                    return new NativeStateChangeResult(result.Succeeded, result.Error);
                }
                else
                {
                    succeeded = native.Delete(
                        profile.ComponentDocumentType,
                        id,
                        ref error,
                        ref connection,
                        ref timestamp);
                }
                if (!succeeded)
                {
                    return NativeStateChangeResult.Rejected(
                        Empty(error, "USERPCO 操作失败。"));
                }
                return NativeStateChangeResult.Accepted(error);
            }
            finally
            {
                Shutdown(login);
                Release(connection);
                Release(position);
                Release(body);
                Release(head);
                Release(voucher);
                Release(login);
            }
        }

        private static string InnermostMessage(Exception exception)
        {
            Exception current = exception;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }
            return Empty(current.Message, exception.Message);
        }

        private static string FirstAttribute(string xml, string name)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return null;
            }
            XAttribute attribute = XDocument.Parse(xml)
                .Descendants()
                .Where(x => string.Equals(
                    x.Name.LocalName,
                    "row",
                    StringComparison.OrdinalIgnoreCase))
                .Attributes()
                .FirstOrDefault(x => string.Equals(
                    x.Name.LocalName,
                    name,
                    StringComparison.OrdinalIgnoreCase));
            return attribute?.Value;
        }

        private static string ChangePurchaseState(
            U8ConnectionOptions options,
            U8DocumentProfile profile,
            long voucherId,
            string operation)
        {
            U8Login.clsLogin login = null;
            object common = null;
            Connection connection = null;
            clsVoucherCO_PU voucher = null;
            IXMLDOMDocument2 head = null;
            IXMLDOMDocument2 body = null;
            IXMLDOMDocument2 current = null;
            try
            {
                login = Login(options);
                common = Create("USCOMMON.Login");
                dynamic commonLogin = common;
                U8Login.clsLogin source = login;
                commonLogin.Load(ref source);
                connection = ConvertConnection(commonLogin.AccountConnection);
                voucher = new clsVoucherCO_PUClass();
                ClsS_Infor information = null;
                bool positive = true;
                string billType = string.Empty;
                string businessType = profile.BusinessType ?? string.Empty;
                UseMode useMode = UseMode.CS;
                voucher.Init(
                    (vouchertype)2,
                    ref login,
                    ref connection,
                    ref information,
                    ref positive,
                    ref billType,
                    ref businessType,
                    ref useMode,
                    businessType,
                    string.Empty);
                head = new DOMDocument26Class();
                body = new DOMDocument26Class();
                string locateWhere = string.Empty;
                object id = voucherId;
                voucher.GetVoucherDataById(
                    ref head,
                    ref body,
                    string.Empty,
                    id,
                    ref locateWhere);

                if (string.Equals(operation, DocumentOperationCodes.Verify,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return voucher.ConfirmArr(head);
                }
                if (string.Equals(operation, DocumentOperationCodes.Unverify,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return voucher.CancelconfirmArr(head);
                }
                return voucher.Delete(head, body, ref current);
            }
            finally
            {
                Shutdown(login);
                Release(current);
                Release(body);
                Release(head);
                Release(voucher);
                Release(connection);
                Release(common);
                Release(login);
            }
        }

        private static string ChangeSalesState(
            U8ConnectionOptions options,
            long voucherId,
            string operation)
        {
            U8XmlPacket packet = QuerySales(options, voucherId);
            U8Login.clsLogin login = null;
            object common = null;
            Connection connection = null;
            ClsVoucherCO_SA voucher = null;
            IXMLDOMDocument2 head = null;
            IXMLDOMDocument2 body = null;
            object system = null;
            try
            {
                head = ParseXml(packet.HeaderXml, "销售单表头");
                body = ParseXml(packet.BodyXml, "销售单表体");
                login = Login(options);
                common = Create("USCOMMON.Login");
                dynamic commonLogin = common;
                U8Login.clsLogin source = login;
                commonLogin.Load(ref source);
                connection = ConvertConnection(commonLogin.AccountConnection);
                voucher = new ClsVoucherCO_SAClass();
                if (!voucher.InitSys(ref login, VoucherTypeSA.DispatchBlue, ref system))
                {
                    throw new InvalidOperationException("销售 CO 初始化失败。");
                }
                string userMode = "CS";
                voucher.Init(
                    VoucherTypeSA.DispatchBlue,
                    ref login,
                    ref connection,
                    ref userMode,
                    ref system);
                if (string.Equals(operation, DocumentOperationCodes.Delete,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return voucher.Delete(ref head, ref body);
                }
                bool verify = string.Equals(
                    operation,
                    DocumentOperationCodes.Verify,
                    StringComparison.OrdinalIgnoreCase);
                return voucher.VerifyVouch(ref head, ref verify);
            }
            finally
            {
                Shutdown(login);
                Release(system);
                Release(body);
                Release(head);
                Release(voucher);
                Release(connection);
                Release(common);
                Release(login);
            }
        }

        private static IXMLDOMDocument2 ParseXml(string xml, string label)
        {
            IXMLDOMDocument2 document = new DOMDocument26Class();
            if (string.IsNullOrWhiteSpace(xml) || !document.loadXML(xml))
            {
                string reason = document.parseError?.reason;
                Release(document);
                throw new InvalidOperationException(
                    label + " XML 无效：" + Empty(reason, "内容为空。"));
            }
            return document;
        }

        private static string SetFirstRowAttribute(
            string xml,
            string attributeName,
            string value)
        {
            XDocument document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            XElement row = document.Descendants().FirstOrDefault(x => string.Equals(
                x.Name.LocalName, "row", StringComparison.OrdinalIgnoreCase));
            if (row == null)
                throw new InvalidOperationException("表头 XML 中找不到 row 节点。");
            row.SetAttributeValue(attributeName, value);
            return document.ToString(SaveOptions.DisableFormatting);
        }

        private static U8XmlPacket QueryInventory(
            U8ConnectionOptions options,
            U8DocumentProfile profile,
            long voucherId)
        {
            U8Login.clsLogin login = null;
            object voucher = null;
            object head = null;
            object body = null;
            object position = null;
            try
            {
                login = Login(options);
                voucher = Create("USERPCO.VoucherCO");
                dynamic native = voucher;
                string error = string.Empty;
                if (!native.IniLogin(login, ref error))
                {
                    throw new InvalidOperationException(Empty(error, "USERPCO 初始化失败。"));
                }
                head = Create("MSXML2.DOMDocument.3.0");
                body = Create("MSXML2.DOMDocument.3.0");
                position = Create("MSXML2.DOMDocument.3.0");
                bool loaded = native.Load(
                    profile.ComponentDocumentType,
                    "ID=" + voucherId,
                    ref head,
                    ref body,
                    ref position,
                    ref error,
                    false,
                    string.Empty);
                if (!loaded)
                {
                    throw new InvalidOperationException(Empty(error, "U8 未返回单据。"));
                }
                return Packet(head, body, position);
            }
            finally
            {
                Shutdown(login);
                Release(position);
                Release(body);
                Release(head);
                Release(voucher);
                Release(login);
            }
        }

        private static U8XmlPacket QueryPurchase(
            U8ConnectionOptions options,
            U8DocumentProfile profile,
            long voucherId)
        {
            U8Login.clsLogin login = null;
            object common = null;
            Connection connection = null;
            clsVoucherCO_PU voucher = null;
            IXMLDOMDocument2 head = null;
            IXMLDOMDocument2 body = null;
            try
            {
                login = Login(options);
                common = Create("USCOMMON.Login");
                dynamic commonLogin = common;
                U8Login.clsLogin source = login;
                commonLogin.Load(ref source);
                connection = ConvertConnection(commonLogin.AccountConnection);

                voucher = new clsVoucherCO_PUClass();
                ClsS_Infor information = null;
                bool positive = true;
                string billType = string.Empty;
                string businessType = profile.BusinessType ?? string.Empty;
                UseMode useMode = UseMode.CS;
                voucher.Init(
                    (vouchertype)2,
                    ref login,
                    ref connection,
                    ref information,
                    ref positive,
                    ref billType,
                    ref businessType,
                    ref useMode,
                    businessType,
                    string.Empty);

                head = new DOMDocument26Class();
                body = new DOMDocument26Class();
                string locateWhere = string.Empty;
                object id = voucherId;
                voucher.GetVoucherDataById(
                    ref head,
                    ref body,
                    string.Empty,
                    id,
                    ref locateWhere);
                U8XmlPacket packet = Packet(head, body, null);
                if (string.IsNullOrWhiteSpace(packet.HeaderXml) &&
                    string.IsNullOrWhiteSpace(packet.BodyXml))
                {
                    throw new InvalidOperationException("U8 未返回单据。");
                }
                return packet;
            }
            finally
            {
                Shutdown(login);
                Release(body);
                Release(head);
                Release(voucher);
                Release(connection);
                Release(common);
                Release(login);
            }
        }

        private static U8XmlPacket QuerySales(
            U8ConnectionOptions options,
            long voucherId)
        {
            U8Login.clsLogin login = null;
            object common = null;
            object connection = null;
            object system = null;
            object voucher = null;
            object head = null;
            object body = null;
            try
            {
                login = Login(options);
                common = Create("USCOMMON.Login");
                dynamic commonLogin = common;
                U8Login.clsLogin source = login;
                commonLogin.Load(ref source);
                connection = commonLogin.AccountConnection;

                system = Create("USSAServer.clsSystem");
                dynamic salesSystem = system;
                string error = string.Empty;
                if (!salesSystem.Init(ref login, ref error))
                {
                    throw new InvalidOperationException(Empty(error, "销售系统初始化失败。"));
                }
                voucher = Create("USSAServer.clsVouchLoad");
                dynamic native = voucher;
                native.clsSys = system;
                head = Create("MSXML2.DOMDocument.3.0");
                body = Create("MSXML2.DOMDocument.3.0");
                bool authorize = true;
                string where = "DLID=" + voucherId;
                short showStyle = 0;
                object id = voucherId;
                bool loaded = native.GetVoucherDataWEB(
                    ref head,
                    ref body,
                    connection,
                    5,
                    false,
                    ref error,
                    id,
                    ref authorize,
                    ref where,
                    ref showStyle);
                if (!loaded)
                {
                    throw new InvalidOperationException(Empty(error, "U8 未返回单据。"));
                }
                return Packet(head, body, null);
            }
            finally
            {
                Shutdown(login);
                Release(body);
                Release(head);
                Release(voucher);
                Release(system);
                Release(connection);
                Release(common);
                Release(login);
            }
        }

        private static long ResolveVoucherId(
            U8GatewayRuntimeOptions options,
            U8DocumentProfile profile,
            string voucherCode)
        {
            long? value = TryResolveVoucherId(options, profile, voucherCode);
            if (!value.HasValue)
            {
                throw new InvalidOperationException("单据不存在。");
            }
            return value.Value;
        }

        private static long? TryResolveVoucherId(
            U8GatewayRuntimeOptions options,
            U8DocumentProfile profile,
            string voucherCode)
        {
            if (string.IsNullOrWhiteSpace(voucherCode))
            {
                throw new ArgumentException("单据号不能为空。", nameof(voucherCode));
            }
            string codeColumn = CodeColumn(profile.DocumentType);
            string idColumn = string.Equals(profile.DocumentType, DocumentTypeCodes.SalesDelivery,
                StringComparison.OrdinalIgnoreCase) ? "DLID" : "ID";
            string sql = "select top (1) [" + idColumn + "] from [dbo].[" +
                profile.HeaderTable + "] where [" + codeColumn + "]=@code order by [" +
                idColumn + "] desc";
            using (var connection = new SqlConnection(ConnectionString(options)))
            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@code", voucherCode.Trim());
                connection.Open();
                object value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                {
                    return null;
                }
                return Convert.ToInt64(value);
            }
        }

        private static long? WaitForVoucherId(
            U8GatewayRuntimeOptions options,
            U8DocumentProfile profile,
            string voucherCode)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                long? value = TryResolveVoucherId(options, profile, voucherCode);
                if (value.HasValue) return value;
                if (attempt < 9) Thread.Sleep(100);
            }
            return null;
        }

        private static VoucherState ReadVoucherState(
            U8GatewayRuntimeOptions options,
            U8DocumentProfile profile,
            long voucherId)
        {
            string idColumn = string.Equals(
                profile.DocumentType,
                DocumentTypeCodes.SalesDelivery,
                StringComparison.OrdinalIgnoreCase)
                ? "DLID"
                : "ID";
            string verifiedColumn = VerifiedColumn(profile.DocumentType);
            string sql = "select top (1) [" + verifiedColumn + "], " +
                "master.dbo.fn_varbintohexstr([ufts]) from [dbo].[" +
                profile.HeaderTable + "] where [" + idColumn + "]=@id";
            using (var connection = new SqlConnection(ConnectionString(options)))
            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@id", voucherId);
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        return new VoucherState { Exists = false };
                    }
                    object verified = reader.GetValue(0);
                    return new VoucherState
                    {
                        Exists = true,
                        Verified = verified != null &&
                            verified != DBNull.Value &&
                            !string.IsNullOrWhiteSpace(Convert.ToString(verified)),
                        Version = reader.IsDBNull(1)
                            ? null
                            : reader.GetString(1)
                    };
                }
            }
        }

        private static VoucherState WaitForState(
            U8GatewayRuntimeOptions options,
            U8DocumentProfile profile,
            long voucherId,
            bool deleted,
            bool verified)
        {
            VoucherState state = null;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                state = ReadVoucherState(options, profile, voucherId);
                if ((deleted && !state.Exists) ||
                    (!deleted && state.Exists && state.Verified == verified))
                {
                    return state;
                }
                if (attempt < 9)
                {
                    Thread.Sleep(100);
                }
            }
            return state ?? new VoucherState { Exists = false };
        }

        private static string VerifiedColumn(string documentType)
        {
            if (string.Equals(documentType, DocumentTypeCodes.Transfer,
                StringComparison.OrdinalIgnoreCase))
                return "cVerifyPerson";
            if (string.Equals(documentType, DocumentTypeCodes.InventoryCount,
                StringComparison.OrdinalIgnoreCase))
                return "cAccounter";
            if (string.Equals(documentType, DocumentTypeCodes.PurchaseArrival,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(documentType, DocumentTypeCodes.SubcontractArrival,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(documentType, DocumentTypeCodes.SalesDelivery,
                    StringComparison.OrdinalIgnoreCase))
                return "cVerifier";
            return "cHandler";
        }

        private static string ConnectionString(U8GatewayRuntimeOptions options)
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = options.SqlServer,
                InitialCatalog = options.SqlDatabase,
                UserID = options.SqlUser,
                Password = options.SqlPassword,
                ConnectTimeout = 15,
                Encrypt = false,
                TrustServerCertificate = true
            }.ConnectionString;
        }

        private static string CodeColumn(string documentType)
        {
            if (string.Equals(documentType, DocumentTypeCodes.Transfer, StringComparison.OrdinalIgnoreCase))
                return "cTVCode";
            if (string.Equals(documentType, DocumentTypeCodes.LocationAdjustment, StringComparison.OrdinalIgnoreCase))
                return "cVouchCode";
            if (string.Equals(documentType, DocumentTypeCodes.InventoryCount, StringComparison.OrdinalIgnoreCase))
                return "cCVCode";
            if (string.Equals(documentType, DocumentTypeCodes.SalesDelivery, StringComparison.OrdinalIgnoreCase))
                return "cDLCode";
            return "cCode";
        }

        private static U8Login.clsLogin Login(U8ConnectionOptions options)
        {
            var login = new U8Login.clsLogin();
            DateTime date = options.LoginDate == default(DateTime)
                ? BusinessClock.Today
                : options.LoginDate.Date;
            if (!login.Login(
                options.Subsystem,
                U8ConnectionTester.NormalizeAccountId(options.AccountId),
                options.Year,
                options.User,
                options.Password ?? string.Empty,
                date.ToString("yyyy-MM-dd"),
                options.Server,
                options.Serial ?? string.Empty))
            {
                string error = Convert.ToString(login.ShareString);
                Shutdown(login);
                Release(login);
                throw U8LoginException.FromMessage(error);
            }
            return login;
        }

        private static object Create(string progId)
        {
            Type type = Type.GetTypeFromProgID(progId, true);
            return Activator.CreateInstance(type);
        }

        private static Connection ConvertConnection(object value)
        {
            IntPtr unknown = IntPtr.Zero;
            try
            {
                unknown = Marshal.GetIUnknownForObject(value);
                return (Connection)Marshal.GetTypedObjectForIUnknown(
                    unknown,
                    typeof(Connection));
            }
            finally
            {
                if (unknown != IntPtr.Zero)
                {
                    Marshal.Release(unknown);
                }
            }
        }

        private static U8XmlPacket Packet(object head, object body, object position)
        {
            return new U8XmlPacket
            {
                HeaderXml = Xml(head),
                BodyXml = Xml(body),
                PositionXml = Xml(position)
            };
        }

        private static string Xml(object document)
        {
            if (document == null) return null;
            dynamic value = document;
            string xml = Convert.ToString(value.xml);
            return string.IsNullOrWhiteSpace(xml) ? null : xml;
        }

        private static void Shutdown(U8Login.clsLogin login)
        {
            if (login == null) return;
            try { login.ShutDown(); } catch { }
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try { Marshal.FinalReleaseComObject(value); } catch { }
            }
        }

        private static void ValidateOptions(U8GatewayRuntimeOptions options)
        {
            if (options?.U8 == null ||
                string.IsNullOrWhiteSpace(options.SqlServer) ||
                string.IsNullOrWhiteSpace(options.SqlDatabase) ||
                string.IsNullOrWhiteSpace(options.SqlUser) ||
                string.IsNullOrWhiteSpace(options.SqlPassword))
            {
                throw new InvalidOperationException("U8 Gateway 尚未完成配置。");
            }
        }

        public static void ValidateWriteOptions(U8GatewayRuntimeOptions options)
        {
            ValidateOptions(options);
            if (!options.EnableWrites)
            {
                throw new InvalidOperationException("U8 写入开关未启用。");
            }
            if (!string.Equals(options.EnvironmentName, "test", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("U8 写入仅允许测试环境。");
            }
            if (string.IsNullOrWhiteSpace(options.WriteAllowedDatabase) ||
                !string.Equals(
                    options.SqlDatabase,
                    options.WriteAllowedDatabase,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("当前数据库不在写入白名单中。");
            }
            if (string.IsNullOrWhiteSpace(options.WriteAllowedAccountId) ||
                !string.Equals(
                    AccountCode(options.U8.AccountId),
                    AccountCode(options.WriteAllowedAccountId),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("当前账套不在写入白名单中。");
            }
        }

        private static string AccountCode(string value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            int separator = trimmed.LastIndexOf('@');
            return separator >= 0 ? trimmed.Substring(separator + 1) : trimmed;
        }

        private static string Empty(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static bool OptionBoolean(
            System.Collections.Generic.IDictionary<string, object> options,
            string name)
        {
            if (options == null || string.IsNullOrWhiteSpace(name)) return false;
            object value;
            if (!options.TryGetValue(name, out value))
            {
                value = options.FirstOrDefault(x => string.Equals(
                    x.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            }
            if (value is bool) return (bool)value;
            bool parsed;
            return value != null && bool.TryParse(Convert.ToString(value), out parsed) && parsed;
        }

        private static string MessageText(object document, string fallback)
        {
            string xml = Xml(document);
            if (string.IsNullOrWhiteSpace(xml)) return fallback;
            try
            {
                XDocument parsed = XDocument.Parse(xml);
                string message = parsed.Descendants()
                    .SelectMany(x => x.Attributes())
                    .Where(x => x.Name.LocalName.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                x.Name.LocalName.IndexOf("msg", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(x => x.Value)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                return Empty(message, fallback);
            }
            catch
            {
                return fallback;
            }
        }

        private static DocumentOperationResult Disabled(string voucherCode)
        {
            return Failure(
                "U8_OPERATION_NOT_ENABLED",
                "该单据操作尚未通过测试账套验证。",
                voucherCode);
        }

        private static DocumentOperationResult Success(
            U8DocumentExecution execution,
            long? voucherId,
            string version,
            string message)
        {
            return new DocumentOperationResult
            {
                Confirmed = true,
                OutcomeKnown = true,
                Code = "OK",
                Message = message,
                VoucherId = voucherId?.ToString(),
                VoucherCode = execution.VoucherCode,
                Version = version
            };
        }

        private static DocumentOperationResult Failure(
            string code,
            string message,
            string voucherCode,
            bool outcomeKnown = true)
        {
            return new DocumentOperationResult
            {
                Confirmed = false,
                OutcomeKnown = outcomeKnown,
                Code = code,
                Message = message,
                VoucherCode = voucherCode
            };
        }

        private sealed class VoucherState
        {
            public bool Exists { get; set; }
            public bool Verified { get; set; }
            public string Version { get; set; }
        }

        private sealed class NativeStateChangeResult
        {
            public NativeStateChangeResult(bool succeeded, string message)
            {
                Succeeded = succeeded;
                Message = message;
            }

            public bool Succeeded { get; }
            public string Message { get; }

            public static NativeStateChangeResult Accepted(string message)
            {
                return new NativeStateChangeResult(true, message);
            }

            public static NativeStateChangeResult Rejected(string message)
            {
                return new NativeStateChangeResult(false, message);
            }
        }

    }
}
