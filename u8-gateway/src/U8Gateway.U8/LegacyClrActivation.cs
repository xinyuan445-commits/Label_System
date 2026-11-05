using System;
using System.Runtime.InteropServices;

namespace U8Gateway.U8
{
    // Web.config startup flags do not configure w3wp.exe. Bind only this Gateway process
    // to its already-running CLR4 before activating U8's legacy managed COM components.
    // https://learn.microsoft.com/dotnet/framework/unmanaged-api/hosting/iclrruntimeinfo-bindaslegacyv2runtime-method
    public static class LegacyClrActivation
    {
        private static readonly Lazy<bool> Ready = new Lazy<bool>(() =>
        {
            var clsid = new Guid("9280188D-0E8E-4867-B30C-7FA83884E8DE");
            var iid = typeof(IMetaHost).GUID;
            IMetaHost host = null; IRuntimeInfo runtime = null;
            try
            {
                Marshal.ThrowExceptionForHR(CLRCreateInstance(ref clsid, ref iid, out host));
                var runtimeIid = typeof(IRuntimeInfo).GUID;
                host.GetRuntime("v4.0.30319", ref runtimeIid, out runtime);
                runtime.BindAsLegacyV2Runtime();
                return true;
            }
            finally
            {
                if (runtime != null) Marshal.ReleaseComObject(runtime);
                if (host != null) Marshal.ReleaseComObject(host);
            }
        });
        public static void Ensure() { _ = Ready.Value; }

        [DllImport("mscoree.dll", ExactSpelling = true)]
        private static extern int CLRCreateInstance(ref Guid clsid, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IMetaHost host);

        [ComImport, Guid("D332DB9E-B9B3-4125-8207-A14884F53216"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMetaHost
        {
            void GetRuntime([MarshalAs(UnmanagedType.LPWStr)] string version, ref Guid iid,
                [MarshalAs(UnmanagedType.Interface)] out IRuntimeInfo runtime);
        }

        [ComImport, Guid("BD39D1D2-BA2F-486A-89B0-B4B0CB466891"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IRuntimeInfo
        {
            void GetVersionString(IntPtr buffer, ref uint length);
            void GetRuntimeDirectory(IntPtr buffer, ref uint length);
            void IsLoaded(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool loaded);
            void LoadErrorString(uint id, IntPtr buffer, ref uint length, int locale);
            void LoadLibrary([MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr module);
            void GetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr address);
            void GetInterface(ref Guid clsid, ref Guid iid, out IntPtr value);
            void IsLoadable([MarshalAs(UnmanagedType.Bool)] out bool loadable);
            void SetDefaultStartupFlags(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string config);
            void GetDefaultStartupFlags(out uint flags, IntPtr config, ref uint length);
            void BindAsLegacyV2Runtime();
        }
    }
}
