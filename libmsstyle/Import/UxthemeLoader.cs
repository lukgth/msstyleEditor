using System;
using System.Runtime.InteropServices;

namespace libmsstyle.Import
{
    /// <summary>
    /// Asks this machine's uxtheme to parse a style the way theme apply does (LoaderLoadTheme, ordinal 92), without
    /// applying it. Only meaningful on windows 10/11; elsewhere the check is skipped.
    /// </summary>
    public static class UxthemeLoader
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string file);
        [DllImport("kernel32")] private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
        [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr h);

        // 19041 signature; build 20000+ dropped the trailing fGlobalTheme. Passing it on 11 is harmless for x64
        // (one extra stack argument the callee never reads).
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int LoaderLoadTheme(IntPtr hFile, IntPtr hInst, string themeName, string color, string size,
            out IntPtr sharable, IntPtr sharableName, int cchSharable, out IntPtr nonSharable, IntPtr nonSharableName, int cchNonSharable,
            IntPtr customLoader, IntPtr reuseSection, int ppi, int langId, bool global);

        /// <returns>null when uxtheme loads the variant, else the HRESULT.</returns>
        public static string TryLoad(string stylePath, string color, string size)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || Environment.OSVersion.Version.Major < 10) return null;
            IntPtr ux = LoadLibrary("uxtheme.dll");
            IntPtr fn = ux == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(ux, (IntPtr)92);
            if (fn == IntPtr.Zero) return null;
            var load = Marshal.GetDelegateForFunctionPointer<LoaderLoadTheme>(fn);
            int hr = load(IntPtr.Zero, IntPtr.Zero, stylePath, color, size, out var sh, IntPtr.Zero, 0, out var nsh, IntPtr.Zero, 0,
                IntPtr.Zero, IntPtr.Zero, 0, 0, false);
            if (sh != IntPtr.Zero) CloseHandle(sh);
            if (nsh != IntPtr.Zero) CloseHandle(nsh);
            return hr >= 0 ? null : $"0x{hr:X8}";
        }
    }
}
