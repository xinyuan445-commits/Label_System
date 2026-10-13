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

        private static void Release(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.FinalReleaseComObject(value); }
            catch { }
        }
    }
}
