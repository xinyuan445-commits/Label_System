using System;
using System.Runtime.InteropServices;
using ADODB;
using MSXML2;
using USERPCO;

namespace U8Gateway.UserpcoBridge
{
    public sealed class UserpcoInsertResult
    {
        public bool Succeeded { get; set; }
        public string Error { get; set; }
        public string VoucherId { get; set; }
    }

    public sealed class UserpcoStateResult
    {
        public bool Succeeded { get; set; }
        public string Error { get; set; }
    }

    public static class UserpcoWriter
    {
        public static UserpcoInsertResult Insert(
            object voucher,
            string voucherType,
            object head,
            object body,
            object position,
            bool redVoucher)
        {
            if (voucher == null) throw new ArgumentNullException(nameof(voucher));

            string error = string.Empty;
            string voucherId = string.Empty;
            Connection connection = null;
            IXMLDOMDocument2 message = null;
            bool check = true;
            bool beforeCheckStock = true;
            try
            {
                bool succeeded = ((VoucherCO)voucher).Insert(
                    voucherType,
                    head,
                    body,
                    position,
                    ref error,
                    ref connection,
                    ref voucherId,
                    ref message,
                    ref check,
                    ref beforeCheckStock,
                    redVoucher,
                    string.Empty,
                    false);
                return new UserpcoInsertResult
                {
                    Succeeded = succeeded,
                    Error = error,
                    VoucherId = voucherId
                };
            }
            finally
            {
                Release(message);
                Release(connection);
            }
        }

        public static UserpcoStateResult ChangeState(
            object voucher,
            string voucherType,
            object voucherId,
            object timestamp,
            bool verify)
        {
            if (voucher == null) throw new ArgumentNullException(nameof(voucher));

            string error = string.Empty;
            Connection connection = null;
            IXMLDOMDocument2 message = null;
            bool check = true;
            bool beforeCheckStock = true;
            bool list = false;
            VBA.Collection makeWheres = null;
            string webXml = string.Empty;
            Scripting.Dictionary generatedVoucherIds = null;
            try
            {
                bool succeeded;
                if (verify)
                {
                    succeeded = ((VoucherCO)voucher).Verify(
                        voucherType,
                        voucherId,
                        ref error,
                        ref connection,
                        ref timestamp,
                        ref message,
                        ref check,
                        ref beforeCheckStock,
                        ref list,
                        ref makeWheres,
                        ref webXml,
                        ref generatedVoucherIds);
                }
                else
                {
                    succeeded = ((VoucherCO)voucher).UnVerify(
                        voucherType,
                        voucherId,
                        ref error,
                        ref connection,
                        ref timestamp,
                        ref message,
                        ref check,
                        ref beforeCheckStock,
                        ref list);
                }
                return new UserpcoStateResult
                {
                    Succeeded = succeeded,
                    Error = error
                };
            }
            finally
            {
                Release(generatedVoucherIds);
                Release(makeWheres);
                Release(message);
                Release(timestamp);
                Release(connection);
            }
        }

        private static void Release(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.FinalReleaseComObject(value); }
            catch { }
        }
    }
}
