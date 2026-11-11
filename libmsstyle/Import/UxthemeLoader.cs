using System;
using System.Runtime.InteropServices;

namespace libmsstyle.Import
{
    /// <summary>what a <see cref="UxthemeLoader.TryLoad"/> call did, so a caller never reports a load that never happened.</summary>
    public enum UxthemeLoadResult
    {
        /// <summary>uxtheme parsed the variant.</summary>
        Loaded,
        /// <summary>this machine can't run the check (not windows 10/11, or no uxtheme ordinal 92).</summary>
        Skipped,
        /// <summary>uxtheme rejected the variant.</summary>
        Failed
    }

    /// <summary>
    /// Asks this machine's uxtheme to parse a style the way theme apply does (LoaderLoadTheme, ordinal 92), without
    /// applying it. Only meaningful on windows 10/11; elsewhere the check is skipped.
    /// </summary>
    public static class UxthemeLoader
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string file);
        [DllImport("kernel32")] private static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32")] private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
        [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("ntdll")] private static extern int RtlGetVersion(ref OsVersionInfo version);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            public int Size;
            public int Major;
            public int Minor;
            public int Build;
            public int Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
            public ushort ServicePackMajor;
            public ushort ServicePackMinor;
            public ushort SuiteMask;
            public byte ProductType;
            public byte Reserved;
        }

        // 19041 signature; build 20000+ dropped the trailing fGlobalTheme. The callee pops its own arguments on
        // x86/arm64, so the surplus argument corrupts the stack there - hence one delegate per shape.
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int LoaderLoadTheme(IntPtr hFile, IntPtr hInst, string themeName, string color, string size,
            out IntPtr sharable, IntPtr sharableName, int cchSharable, out IntPtr nonSharable, IntPtr nonSharableName, int cchNonSharable,
            IntPtr customLoader, IntPtr reuseSection, int ppi, int langId, bool global);

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int LoaderLoadThemeNoGlobal(IntPtr hFile, IntPtr hInst, string themeName, string color, string size,
            out IntPtr sharable, IntPtr sharableName, int cchSharable, out IntPtr nonSharable, IntPtr nonSharableName, int cchNonSharable,
            IntPtr customLoader, IntPtr reuseSection, int ppi, int langId);

        /// <param name="error">null when the variant loaded, else the hresult and why the check didn't pass.</param>
        public static UxthemeLoadResult TryLoad(string stylePath, string color, string size, out string error)
        {
            error = null;
            var os = new OsVersionInfo { Size = Marshal.SizeOf(typeof(OsVersionInfo)) };
            if (RtlGetVersion(ref os) != 0 || os.Platform != 2 || os.Major < 10)
            {
                error = "this machine isn't windows 10/11";
                return UxthemeLoadResult.Skipped;
            }

            IntPtr ux = LoadLibrary("uxtheme.dll");
            if (ux == IntPtr.Zero)
            {
                error = $"uxtheme.dll didn't load (error {Marshal.GetLastWin32Error()})";
                return UxthemeLoadResult.Skipped;
            }
            try
            {
                IntPtr fn = GetProcAddress(ux, (IntPtr)92);
                if (fn == IntPtr.Zero)
                {
                    error = "uxtheme.dll has no ordinal 92";
                    return UxthemeLoadResult.Skipped;
                }

                int hr;
                if (os.Build < 20000)
                {
                    var load = Marshal.GetDelegateForFunctionPointer<LoaderLoadTheme>(fn);
                    hr = load(IntPtr.Zero, IntPtr.Zero, stylePath, color, size, out var sh, IntPtr.Zero, 0, out var nsh, IntPtr.Zero, 0,
                        IntPtr.Zero, IntPtr.Zero, 0, 0, false);
                    if (sh != IntPtr.Zero) CloseHandle(sh);
                    if (nsh != IntPtr.Zero) CloseHandle(nsh);
                }
                else
                {
                    var load = Marshal.GetDelegateForFunctionPointer<LoaderLoadThemeNoGlobal>(fn);
                    hr = load(IntPtr.Zero, IntPtr.Zero, stylePath, color, size, out var sh, IntPtr.Zero, 0, out var nsh, IntPtr.Zero, 0,
                        IntPtr.Zero, IntPtr.Zero, 0, 0);
                    if (sh != IntPtr.Zero) CloseHandle(sh);
                    if (nsh != IntPtr.Zero) CloseHandle(nsh);
                }

                // success is SUCCEEDED(hr): S_FALSE (1) is a success with detail, not a rejection.
                if (hr >= 0) return UxthemeLoadResult.Loaded;
                error = $"0x{hr:X8}";
                return UxthemeLoadResult.Failed;
            }
            finally
            {
                FreeLibrary(ux);
            }
        }
    }
}
