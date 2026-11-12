using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace libmsstyle.Import
{
    /// <summary>
    /// Applies a generated .theme without any UI. The shell handler (<c>themecpl.dll,OpenThemeAction</c>, the "open" verb of
    /// a .theme) applies everything but opens Settings while it does, and how much of it lands depends on the process it is
    /// started from. This does the same work in-process: the visual style through uxtheme's own
    /// <c>SetSystemVisualStyle</c> (the call the editor previews with), then what Windows otherwise takes from the style and
    /// the .theme — the style's SysMetrics colors, sizes and fonts (which is what themeui copies into NONCLIENTMETRICS and
    /// the WindowMetrics registry), the .theme's [Metrics] blobs on top, the desktop picture and screensaver, cursors, sound
    /// events and per-CLSID default icons. Every step reports what it did or skipped, so a half-applied theme is visible
    /// instead of silent.
    /// </summary>
    public class ThemeApplyResult
    {
        public bool Ok { get; internal set; }
        public List<string> Notes { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public string CurrentTheme { get; internal set; }
        public string DllName { get; internal set; }
    }

    public static class XpThemeApply
    {
        [DllImport("uxtheme.dll", EntryPoint = "#65", CharSet = CharSet.Unicode)]
        private static extern uint SetSystemVisualStyle(string themeFile, string colorName, string sizeName, uint unknownFlags);

        [DllImport("user32.dll")]
        private static extern bool SetSysColors(int cElements, int[] lpaElements, int[] lpaRgbValues);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref NONCLIENTMETRICSW pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref LOGFONTW pvParam, uint fWinIni);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHLoadIndirectString(string source, StringBuilder output, int cchOut, IntPtr reserved);

        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPI_SETSCREENSAVEACTIVE = 0x0011;
        private const uint SPI_SETICONTITLELOGFONT = 0x0022;
        private const uint SPI_GETNONCLIENTMETRICS = 0x0029;
        private const uint SPI_SETNONCLIENTMETRICS = 0x002A;
        private const uint SPI_SETCURSORS = 0x0057;
        private const uint SPI_SETFLATMENU = 0x1023;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;

        private const int SHCNE_ASSOCCHANGED = 0x08000000;

        private const string WindowMetrics = @"Control Panel\Desktop\WindowMetrics";

        // xp's theme engine converts the 96-dpi numbers stored in themes to the session dpi before applying them
        // (themeui!DPIConvert_SystemMetricsAll) and its font sizes are points, not pixels.
        [DllImport("user32.dll")]
        private static extern int GetDpiForSystem();

        private static int m_dpi;

        /// <summary>the session dpi (96 on an unscaled display). GetDpiForSystem needs Windows 10 1607; the fallback
        /// reads the desktop's DPI, which is the same number for a system-dpi-aware process.</summary>
        private static int Dpi
        {
            get
            {
                if (m_dpi != 0) return m_dpi;
                try { m_dpi = GetDpiForSystem(); }
                catch (EntryPointNotFoundException) { m_dpi = 0; }
                if (m_dpi <= 0)
                {
                    using (var g = Graphics.FromHwnd(IntPtr.Zero)) m_dpi = (int)Math.Round(g.DpiY);
                }
                return m_dpi > 0 ? m_dpi : 96;
            }
        }

        private static int MulDiv(int number, int numerator, int denominator)
        {
            return (int)Math.Round((double)number * numerator / denominator, MidpointRounding.AwayFromZero);
        }

        /// <summary>converts a theme's 96-dpi pixel value to this session's dpi.</summary>
        private static int From96(int px)
        {
            return MulDiv(px, Dpi, 96);
        }

        /// <summary>the cursor slots xp's theme engine writes, and only these
        /// (themeui/themefile.cpp:19 <c>s_pszCursorArray</c>). Every other slot in
        /// <see cref="XpThemeFiles.CursorKeys"/> is a win10 addition xp never had: writing them blanks the user's own
        /// cursors, so they are read but never touched. The list is the same 15 slots desk.cpl joins into its comma
        /// list (<see cref="SchemeSlots"/>, shell/cpls/main/mouseptr.c:155 <c>gacd[]</c>): Hand is the pointer's
        /// link/hand slot there, and Link is a win10-only duplicate of it that xp never wrote.</summary>
        private static readonly string[] XpCursorSlots =
        {
            "Arrow", "Help", "AppStarting", "Wait", "NWPen", "No", "SizeNS", "SizeWE", "Crosshair", "IBeam",
            "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand",
        };

        /// <summary>the scheme's slots in the order desk.cpl joins them into its comma list
        /// (shell/cpls/main/mouseptr.c:155 <c>gacd[]</c>, ends with Hand).</summary>
        private static readonly string[] SchemeSlots =
        {
            "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No", "SizeNS", "SizeWE",
            "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand",
        };

        /// <summary>SysMetrics sizes (TMT 1201..1210) -> WindowMetrics value names, and whether the registry keeps them
        /// as a negative pixel count in twips (everything except PaddedBorderWidth).</summary>
        private static readonly (int Id, string Name, bool Negative)[] SizeMetrics =
        {
            (1201, "BorderWidth", true),
            (1202, "ScrollWidth", true),
            (1203, "ScrollHeight", true),
            (1204, "CaptionWidth", true),
            (1205, "CaptionHeight", true),
            (1206, "SmCaptionWidth", true),
            (1207, "SmCaptionHeight", true),
            (1208, "MenuWidth", true),
            (1209, "MenuHeight", true),
            (1210, "PaddedBorderWidth", false),
        };

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LOGFONTW
        {
            public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
            public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NONCLIENTMETRICSW
        {
            public uint cbSize;
            public int iBorderWidth, iScrollWidth, iScrollHeight, iCaptionWidth, iCaptionHeight;
            public LOGFONTW lfCaptionFont;
            public int iSmCaptionWidth, iSmCaptionHeight;
            public LOGFONTW lfSmCaptionFont;
            public int iMenuWidth, iMenuHeight;
            public LOGFONTW lfMenuFont, lfStatusFont, lfMessageFont;
            public int iPaddedBorderWidth;
        }

        /// <summary>
        /// Applies <paramref name="themePath"/> (a .theme installed under %WINDIR%\Resources\Themes) without opening any
        /// Windows UI. Returns notes for each section, and <see cref="ThemeApplyResult.Ok"/> only when the style was
        /// accepted by uxtheme <em>and</em> every step after it succeeded: a caller scripting the apply needs "the theme
        /// is applied", not "the style was switched".
        /// </summary>
        public static ThemeApplyResult Apply(string themePath, bool styleOnly = false)
        {
            var r = new ThemeApplyResult();
            if (!File.Exists(themePath)) throw new FileNotFoundException("theme file not found", themePath);
            var ini = ThemeIni.Read(themePath);

            string stylePath = Expand(ini.Get("VisualStyles", "Path"), themePath);
            string color = ini.Get("VisualStyles", "ColorStyle") ?? "NormalColor";
            string size = ini.Get("VisualStyles", "Size") ?? "NormalSize";
            if (string.IsNullOrEmpty(stylePath) || !File.Exists(stylePath))
            {
                r.Warnings.Add($"style: \"{stylePath}\" not found; the theme's [VisualStyles] Path must name an installed .msstyles");
                return r;
            }

            // 1. the visual style itself: uxtheme's own loader, no UI, same call the editor previews with.
            // uxtheme reports success-with-detail as S_FALSE, so the test is SUCCEEDED(hr), not hr == 0
            // (themeui/util.cpp:2062-2090).
            uint hr = SetSystemVisualStyle(stylePath, color, size, 0);
            if (hr > 0x7FFFFFFF)
            {
                string msg;
                try
                {
                    var ex = Marshal.GetExceptionForHR(unchecked((int)(hr & 0x87FFFFFF)));
                    msg = ex?.Message ?? "HRESULT 0x" + hr.ToString("X8");
                }
                catch (Exception) { msg = "HRESULT 0x" + hr.ToString("X8"); }
                r.Warnings.Add($"style: uxtheme rejected {Path.GetFileName(stylePath)} ({msg}); nothing else applied");
                return r;
            }
            r.Ok = true;
            r.Notes.Add($"style: {Path.GetFileName(stylePath)} ({color}, {size})");
            if (styleOnly)
            {
                // the style is the only step that matters when iterating on a port: colors, desktop picture, metrics and
                // icons each broadcast WM_SETTINGCHANGE and take tens of seconds with a busy desktop.
                r.Notes.Add("style-only: colors, desktop, metrics, cursors, sounds and icons kept");
                return r;
            }

            // windows swaps the style on its own thread; touching metrics/colors before it is done lets it clobber them
            // (the editor's preview waits the same 500 ms for this).
            System.Threading.Thread.Sleep(500);

            // 2. sysmetrics: win32k reads them from the registry/SPI values, not from the style, so themeui copies the
            //    style's colors, sizes and fonts into the session. Do the same, sizes and fonts first: the WM_SETTINGCHANGE
            //    they broadcast can make the shell re-read colors. The live read is a prerequisite: xp returns its
            //    failure and stops the chain (themeui/themefile.cpp:225-226, :655-690) rather than persisting zeros.
            var ncm = GetNonclientMetrics();
            if (ncm == null)
            {
                r.Warnings.Add("sysmetrics: SPI_GETNONCLIENTMETRICS failed; the style's metrics, the .theme's metrics and the colors were left alone");
                r.Ok = false;
                return r;
            }
            var live = ncm.Value;
            var sm = ReadStyleMetrics(stylePath, color, size, ref live, r);
            if (sm.Failed) r.Ok = false;
            if (sm.NcmModified) r.Ok &= ApplyAndPersistNcm(live, r);
            if (sm.IconTitleFont.HasValue) r.Ok &= ApplyIconTitleFont(sm.IconTitleFont.Value, r);

            // 3. the .theme's own [Metrics] blobs: an xp pack's theme can override the style's numbers, and the blobs are
            //    the same shape ("Windows Classic.theme" of the working ports uses them).
            r.Ok &= ApplyThemeMetrics(ini, live.iPaddedBorderWidth, r);

            // 4. colors, from the .theme's [Control Panel\Colors] with the style's SysMetrics as the base: xp seeds the
            //    struct from the style and then overwrites it from the .theme (themefile.cpp:190-232, :349-386).
            r.Ok &= ApplyColors(sm.Colors, ini, r);

            // 5. desktop: picture, fit, screensaver.
            r.Ok &= ApplyDesktop(ini, themePath, r);

            // 6. cursors, sound events and per-CLSID default icons (shell settings, not style records).
            r.Ok &= ApplyCursors(ini, themePath, r);
            r.Ok &= ApplySounds(ini, themePath, r);
            r.Ok &= ApplyIcons(ini, themePath, r);
            r.Ok &= ApplyModes(ini, r);

            // 7. current theme, so desk.cpl and Personalization name what is applied.
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes"))
                    k.SetValue("CurrentTheme", themePath, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                r.Warnings.Add("theme: could not write Themes\\CurrentTheme: " + ex.Message);
                r.Ok = false;
            }

            r.CurrentTheme = ReadString(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes", "CurrentTheme");
            r.DllName = Expand(ReadString(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ThemeManager", "DllName"), themePath);
            r.Notes.Add($"current: {r.CurrentTheme}");
            r.Notes.Add($"style in use: {r.DllName}");
            return r;
        }

        /// <summary>Applies only explicit icon and app/system mode entries, without style/metrics broadcasts or
        /// changing desktop, cursor, sound or current-theme settings. Useful after a safe style-only install.</summary>
        public static ThemeApplyResult ApplyAppearanceExtras(string themePath)
        {
            if (!File.Exists(themePath)) throw new FileNotFoundException("theme file not found", themePath);
            var ini = ThemeIni.Read(themePath);
            var result = new ThemeApplyResult { Ok = true };
            result.Ok &= ApplyIcons(ini, themePath, result);
            result.Ok &= ApplyModes(ini, result);
            result.Notes.Add("appearance-only: style, system palette/metrics, wallpaper, cursors, sounds and current-theme selection kept");
            return result;
        }

        private static bool ApplyModes(ThemeIni ini, ThemeApplyResult result)
        {
            bool ok = true;
            bool changed = false;
            foreach (var pair in new[] { (Theme: "AppMode", Registry: "AppsUseLightTheme"), (Theme: "SystemMode", Registry: "SystemUsesLightTheme") })
            {
                string mode = ini.Get("VisualStyles", pair.Theme);
                if (string.IsNullOrWhiteSpace(mode)) continue;
                mode = mode.Trim();
                if (!mode.Equals("Light", StringComparison.OrdinalIgnoreCase) && !mode.Equals("Dark", StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add($"mode: {pair.Theme} must be Light or Dark; existing mode kept");
                    ok = false;
                    continue;
                }
                try
                {
                    int value = mode.Equals("Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        key.SetValue(pair.Registry, value, RegistryValueKind.DWord);
                        if (!Equals(key.GetValue(pair.Registry), value)) throw new IOException("registry read-back differs");
                    }
                    changed = true;
                    result.Notes.Add($"mode: {pair.Theme}={mode} ({pair.Registry}={value})");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException)
                {
                    result.Warnings.Add($"mode: {pair.Theme} could not be applied ({e.Message})");
                    ok = false;
                }
            }
            // Bounded notification: an unresponsive unrelated window cannot hang a selective apply.
            if (changed)
            {
                var section = Marshal.StringToHGlobalUni("ImmersiveColorSet");
                try { SendMessageTimeout(new IntPtr(0xffff), 0x001a, IntPtr.Zero, section, 0x0002, 2000, out _); }
                finally { Marshal.FreeHGlobal(section); }
            }
            return ok;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        private sealed class StyleMetrics
        {
            public List<KeyValuePair<int, int>> Colors { get; } = new List<KeyValuePair<int, int>>();
            public bool NcmModified;
            public LOGFONTW? IconTitleFont;
            /// <summary>the style or one of its applied variants couldn't be read, so what is here is incomplete.</summary>
            public bool Failed;
        }

        /// <summary>reads the applied style's "sysmetrics" class for the color/size pair that was just set: the base
        /// VARIANT/NORMAL records plus the overrides in the color and size variants (a size variant carries only the
        /// font records that differ from the normal size). Same source themeui uses; the SemanticModel is not available
        /// here, the raw class is what the OS reads too.</summary>
        private static StyleMetrics ReadStyleMetrics(string stylePath, string color, string size, ref NONCLIENTMETRICSW ncm, ThemeApplyResult r)
        {
            var sm = new StyleMetrics();
            try
            {
                var style = new VisualStyle();
                style.Load(stylePath);
                using (style)
                {
                    foreach (var cls in style.Classes.Values.Where(c => c.ClassName.Equals("sysmetrics", StringComparison.OrdinalIgnoreCase)))
                        foreach (var part in cls.Parts.Values)
                            foreach (var state in part.States.Values)
                                foreach (var prop in state.Properties)
                                    ReadSystemMetric(prop, style.PreferredStringTable, ref ncm, sm, r);

                    // NORMAL already holds the normal-size records; every other applied variant only lists what differs.
                    var variants = AppliedVariants(stylePath, color, size, r);
                    if (variants == null) sm.Failed = true;
                    else
                        foreach (var variant in variants)
                        {
                            var overrides = VariantProperties(stylePath, variant, r);
                            if (overrides == null) { sm.Failed = true; continue; }
                            foreach (var prop in overrides)
                            {
                                if (!style.Classes.TryGetValue(prop.Header.classID, out StyleClass cls)
                                    || !cls.ClassName.Equals("sysmetrics", StringComparison.OrdinalIgnoreCase)) continue;
                                ReadSystemMetric(prop, style.PreferredStringTable, ref ncm, sm, r);
                            }
                        }
                }
            }
            catch (Exception ex)
            {
                r.Warnings.Add($"sysmetrics: can't read {Path.GetFileName(stylePath)} ({color}, {size}): {ex.Message}; the style's colors, sizes and fonts were not applied");
                sm.Failed = true;
            }
            return sm;
        }

        /// <summary>the VARIANT resources uxtheme loads for (color, size) on top of NORMAL (the base variant carries the
        /// normal color and size already); null when the maps can't be read, which leaves the style's overrides unknown.</summary>
        private static List<string> AppliedVariants(string stylePath, string color, string size, ThemeApplyResult r)
        {
            var names = new List<string>();
            IntPtr mod = IntPtr.Zero;
            try
            {
                mod = Win32Api.LoadLibraryEx(stylePath, IntPtr.Zero, Win32Api.LoadLibraryFlags.LOAD_LIBRARY_AS_DATAFILE);
                if (mod == IntPtr.Zero) throw new IOException("can't open it");
                byte[] cmap = ResourceAccess.GetResource(mod, "CMAP", "CMAP");
                byte[] bcmap = ResourceAccess.GetResource(mod, "BCMAP", "BCMAP");
                byte[] vmap = ResourceAccess.GetResource(mod, "VMAP", "VMAP");
                byte[] rmap = ResourceAccess.GetResource(mod, "RMAP", "RMAP");
                if (cmap == null || vmap == null || rmap == null) return names;
                var maps = VariantMaps.Read(cmap, bcmap, vmap, rmap);
                foreach (var v in maps.Variants)
                {
                    if (v.Variant.Equals("NORMAL", StringComparison.OrdinalIgnoreCase)) continue;
                    bool colorMatch = string.IsNullOrEmpty(color) || v.Color.Equals(color, StringComparison.OrdinalIgnoreCase);
                    bool sizeMatch = string.IsNullOrEmpty(size) || v.Size.Equals(size, StringComparison.OrdinalIgnoreCase);
                    if (colorMatch && sizeMatch) names.Add(v.Variant);
                }
            }
            catch (Exception ex)
            {
                r.Warnings.Add($"sysmetrics: can't read {Path.GetFileName(stylePath)}'s variants ({ex.Message}); only the base variant is applied");
                return null;
            }
            finally { if (mod != IntPtr.Zero) Win32Api.FreeLibrary(mod); }
            return names;
        }

        /// <summary>the records of one VARIANT resource, or null when the stream is damaged: xp fails the load rather
        /// than applying part of a class (uxtheme/loader.cpp:746-786), and a prefix here would keep the base variant's
        /// fonts for every override past the damage without saying so.</summary>
        private static List<StyleProperty> VariantProperties(string stylePath, string variant, ThemeApplyResult r)
        {
            var props = new List<StyleProperty>();
            IntPtr mod = IntPtr.Zero;
            try
            {
                mod = Win32Api.LoadLibraryEx(stylePath, IntPtr.Zero, Win32Api.LoadLibraryFlags.LOAD_LIBRARY_AS_DATAFILE);
                if (mod == IntPtr.Zero) throw new IOException("can't open it");
                byte[] data = ResourceAccess.GetResource(mod, "VARIANT", variant);
                if (data == null) return props;
                int at = 0;
                while (at < data.Length)
                {
                    try { props.Add(PropertyStream.ReadNextProperty(data, ref at)); }
                    catch (PropertyStreamUnknownBytesException ex)
                    {
                        r.Warnings.Add($"sysmetrics: variant {variant} is damaged at byte {at} ({ex.Message}); none of its overrides were applied");
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                r.Warnings.Add($"sysmetrics: can't read variant {variant} ({ex.Message}); none of its overrides were applied");
                return null;
            }
            finally { if (mod != IntPtr.Zero) Win32Api.FreeLibrary(mod); }
            return props;
        }

        private static void ReadSystemMetric(StyleProperty prop, Dictionary<int, string> stringTable, ref NONCLIENTMETRICSW ncm, StyleMetrics sm, ThemeApplyResult r)
        {
            int id = prop.Header.nameID;
            var type = (IDENTIFIER)prop.Header.typeID;

            if (id >= (int)IDENTIFIER.FIRSTCOLOR && id <= (int)IDENTIFIER.LASTCOLOR && type == IDENTIFIER.COLOR)
            {
                object v = prop.GetValue();
                int rgb;
                if (v is Color c) rgb = c.R | (c.G << 8) | (c.B << 16);
                else if (v is int i) rgb = i;
                else return;
                sm.Colors.Add(new KeyValuePair<int, int>(id - (int)IDENTIFIER.FIRSTCOLOR, rgb));
                return;
            }

            if (id >= (int)IDENTIFIER.FIRSTSIZE && id <= (int)IDENTIFIER.LASTSIZE && type == IDENTIFIER.SIZE)
            {
                if (!(prop.GetValue() is int size)) return;
                int px = From96(size);
                switch ((IDENTIFIER)id)
                {
                    case IDENTIFIER.SIZINGBORDERWIDTH: ncm.iBorderWidth = px; sm.NcmModified = true; break;
                    case IDENTIFIER.SCROLLBARWIDTH: ncm.iScrollWidth = px; sm.NcmModified = true; break;
                    case IDENTIFIER.SCROLLBARHEIGHT: ncm.iScrollHeight = px; sm.NcmModified = true; break;
                    case IDENTIFIER.CAPTIONBARWIDTH: ncm.iCaptionWidth = px; sm.NcmModified = true; break;
                    case IDENTIFIER.CAPTIONBARHEIGHT: ncm.iCaptionHeight = px; sm.NcmModified = true; break;
                    case IDENTIFIER.SMCAPTIONBARWIDTH: ncm.iSmCaptionWidth = px; sm.NcmModified = true; break;
                    case IDENTIFIER.SMCAPTIONBARHEIGHT: ncm.iSmCaptionHeight = px; sm.NcmModified = true; break;
                    case IDENTIFIER.MENUBARWIDTH: ncm.iMenuWidth = px; sm.NcmModified = true; break;
                    case IDENTIFIER.MENUBARHEIGHT: ncm.iMenuHeight = px; sm.NcmModified = true; break;
                    case IDENTIFIER.PADDEDBORDERWIDTH: ncm.iPaddedBorderWidth = px; sm.NcmModified = true; break;
                }
                return;
            }

            if (id >= (int)IDENTIFIER.FIRSTFONT && id <= (int)IDENTIFIER.ICONTITLEFONT && type == IDENTIFIER.FONT)
            {
                if (stringTable == null || !stringTable.TryGetValue(prop.Header.shortFlag, out string text)) return;
                LOGFONTW? parsed = ParseFontString(text, out string fontError);
                if (parsed == null)
                {
                    r.Warnings.Add($"sysmetrics: font {id}: {fontError}; keeping the current value");
                    return;
                }
                LOGFONTW lf = parsed.Value;
                switch ((IDENTIFIER)id)
                {
                    case IDENTIFIER.CAPTIONFONT: ncm.lfCaptionFont = lf; sm.NcmModified = true; break;
                    case IDENTIFIER.SMALLCAPTIONFONT: ncm.lfSmCaptionFont = lf; sm.NcmModified = true; break;
                    case IDENTIFIER.MENUFONT: ncm.lfMenuFont = lf; sm.NcmModified = true; break;
                    case IDENTIFIER.STATUSFONT: ncm.lfStatusFont = lf; sm.NcmModified = true; break;
                    case IDENTIFIER.MSGBOXFONT: ncm.lfMessageFont = lf; sm.NcmModified = true; break;
                    case IDENTIFIER.ICONTITLEFONT: sm.IconTitleFont = lf; break;
                }
                return;
            }

            // SPI_SETFLATMENU is a SPIF_BOOL action: ntuser reads the new state from lParam, not uiParam
            // (ntuser/kernel/rare.c:3700-3716), so the value goes in pvParam with uiParam 0 - exactly what every xp
            // caller does (uxtheme/loader.cpp:1085-1091). Passing it in uiParam fails with ERROR_INVALID_PARAMETER and
            // only ever turned flat menus off.
            if (id == (int)IDENTIFIER.FLATMENUS && type == IDENTIFIER.BOOLTYPE && prop.GetValue() is bool flat)
            {
                if (!SystemParametersInfoW(SPI_SETFLATMENU, 0, flat ? new IntPtr(1) : IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
                {
                    r.Warnings.Add($"sysmetrics: SPI_SETFLATMENU {(flat ? "on" : "off")} failed ({Marshal.GetLastWin32Error()})");
                    sm.Failed = true;
                }
            }
        }

        /// <summary>the style's font resource is "Face, Size, [Bold] [Italic] [Underline] [Strikeout] [Quality:X]", the
        /// same grammar uxtheme's _ParseFont reads (FontSpec). Sizes are points; LOGFONT wants pixels at this DPI. A
        /// negative size is a pixel height (XPMC hides the Start button's label with "Arial, -1"). Quality tokens are
        /// honored and an unknown token is refused like uxtheme refuses it, instead of forcing ClearType on everything.
        /// The charset comes from the font spec ([globals] Charset, xp's TMT_CHARSET), defaulting to DEFAULT_CHARSET.</summary>
        private static LOGFONTW? ParseFontString(string text, out string error)
        {
            error = null;
            var spec = FontSpec.Parse(text);
            if (spec.Error != null) { error = $"\"{text}\" isn't a font uxtheme accepts: {spec.Error}"; return null; }
            if (!spec.IsSane) { error = $"\"{text}\" has no usable face or size"; return null; }
            return new LOGFONTW
            {
                lfHeight = spec.Points < 0 ? From96(-spec.Points) : -MulDiv(spec.Points, Dpi, 72),
                lfWeight = spec.Bold ? 700 : 400,
                lfItalic = (byte)(spec.Italic ? 1 : 0),
                lfUnderline = (byte)(spec.Underline ? 1 : 0),
                lfStrikeOut = (byte)(spec.Strikeout ? 1 : 0),
                lfCharSet = (byte)spec.Charset,
                lfQuality = (byte)spec.Quality,
                lfFaceName = spec.Face,
            };
        }

        /// <summary>the live NONCLIENTMETRICS, or null when win32k won't hand it over: a zeroed struct written back is
        /// every metric the style doesn't mention persisted as 0 twips.</summary>
        private static NONCLIENTMETRICSW? GetNonclientMetrics()
        {
            var ncm = new NONCLIENTMETRICSW { cbSize = (uint)Marshal.SizeOf(typeof(NONCLIENTMETRICSW)) };
            return SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, ncm.cbSize, ref ncm, 0) ? ncm : (NONCLIENTMETRICSW?)null;
        }

        private static bool ApplyAndPersistNcm(NONCLIENTMETRICSW ncm, ThemeApplyResult r)
        {
            ncm.cbSize = (uint)Marshal.SizeOf(typeof(NONCLIENTMETRICSW));
            bool ok = SystemParametersInfoW(SPI_SETNONCLIENTMETRICS, ncm.cbSize, ref ncm, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            if (!ok) r.Warnings.Add($"sysmetrics: SPI_SETNONCLIENTMETRICS failed ({Marshal.GetLastWin32Error()})");

            using (var k = Registry.CurrentUser.CreateSubKey(WindowMetrics))
            {
                // the registry keeps sizes as a signed pixel count in twips, negative for the sizes that are subtracted
                // from the client area; PaddedBorderWidth is the one positive.
                foreach (var m in SizeMetrics)
                {
                    int twips = GetAlloc(GetNcmSize(ref ncm, m.Id));
                    k.SetValue(m.Name, (m.Negative ? -twips : twips).ToString(), RegistryValueKind.String);
                }
                k.SetValue("NonclientMetrics", MarshalBytes(ncm), RegistryValueKind.Binary);
                SetFontValue(k, "CaptionFont", ncm.lfCaptionFont);
                SetFontValue(k, "SmCaptionFont", ncm.lfSmCaptionFont);
                SetFontValue(k, "MenuFont", ncm.lfMenuFont);
                SetFontValue(k, "StatusFont", ncm.lfStatusFont);
                SetFontValue(k, "MessageFont", ncm.lfMessageFont);
            }
            return ok;
        }

        private static int GetNcmSize(ref NONCLIENTMETRICSW ncm, int id)
        {
            switch (id)
            {
                case 1201: return ncm.iBorderWidth;
                case 1202: return ncm.iScrollWidth;
                case 1203: return ncm.iScrollHeight;
                case 1204: return ncm.iCaptionWidth;
                case 1205: return ncm.iCaptionHeight;
                case 1206: return ncm.iSmCaptionWidth;
                case 1207: return ncm.iSmCaptionHeight;
                case 1208: return ncm.iMenuWidth;
                case 1209: return ncm.iMenuHeight;
                case 1210: return ncm.iPaddedBorderWidth;
                default: return 0;
            }
        }

        /// <summary>a session-dpi pixel count as the twips NTUSER stores: twips always encode the 96-dpi value
        /// (xp converts against the session's own dpi, ntuser/kernel/rare.c:805-836, so 144 dpi and 10 px is the same
        /// 150 twips as 96 dpi and 10 px). Multiplying the session pixel count by 15 instead scaled every size twice.</summary>
        private static int GetAlloc(int px)
        {
            return MulDiv(px, 1440, Dpi);
        }

        private static void SetFontValue(RegistryKey k, string name, LOGFONTW lf)
        {
            if (string.IsNullOrEmpty(lf.lfFaceName)) return;
            k.SetValue(name, MarshalBytes(lf), RegistryValueKind.Binary);
        }

        private static bool ApplyIconTitleFont(LOGFONTW lf, ThemeApplyResult r)
        {
            bool ok = SystemParametersInfoW(SPI_SETICONTITLELOGFONT, (uint)Marshal.SizeOf(typeof(LOGFONTW)), ref lf, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            using (var k = Registry.CurrentUser.CreateSubKey(WindowMetrics))
                SetFontValue(k, "IconFont", lf);
            if (!ok) r.Warnings.Add($"sysmetrics: SPI_SETICONTITLELOGFONT failed ({Marshal.GetLastWin32Error()})");
            else r.Notes.Add($"sysmetrics: icon title font {lf.lfFaceName}");
            return ok;
        }

        /// <summary>
        /// The .theme's [Metrics] section. XP reads the two blobs for the fonts only: <c>IconMetrics</c> supplies
        /// <c>lfIconTitle</c> and nothing else (themefile.cpp:256-259), <c>NonclientMetrics</c> supplies the nine sizes
        /// and the five fonts, and the six <c>CaptionFont</c>/… descriptors then override those fonts
        /// (themefile.cpp:331-342, <c>_LoadCustomFonts</c>). Icon spacing and <c>iTitleWrap</c> are never applied - xp
        /// takes them from <c>WindowMetrics</c> - and a blob can't zero the style's padded border, which is a Vista+
        /// field the xp struct doesn't have.
        /// </summary>
        private static bool ApplyThemeMetrics(ThemeIni ini, int paddedBorderWidth, ThemeApplyResult r)
        {
            string nc = ini.Get("Metrics", "NonclientMetrics");
            string ic = ini.Get("Metrics", "IconMetrics");
            if (string.IsNullOrEmpty(nc) && string.IsNullOrEmpty(ic) && !XpThemeFiles.MetricFontKeys.Any(k => ini.Get("Metrics", k) != null)) return true;

            bool ok = true;
            NONCLIENTMETRICSW? ncm = null;
            LOGFONTW? iconTitle = null;

            if (!string.IsNullOrEmpty(nc))
            {
                ncm = NonclientAnsiToWide(ParseBytes(nc));
                if (ncm == null) r.Warnings.Add("metrics: NonclientMetrics isn't the xp ANSI blob (cbSize 340); skipped");
                else
                {
                    var n = ncm.Value;
                    n.iPaddedBorderWidth = paddedBorderWidth;
                    ncm = n;
                }
            }
            if (!string.IsNullOrEmpty(ic))
            {
                var lf = IconTitleFontAnsiToWide(ParseBytes(ic));
                if (lf == null) r.Warnings.Add("metrics: IconMetrics isn't the xp ANSI blob (cbSize 76); skipped");
                else iconTitle = lf;
            }

            // the descriptors override the blob's fonts, so they are read after both blobs.
            foreach (var key in XpThemeFiles.MetricFontKeys)
            {
                string v = ini.Get("Metrics", key);
                if (string.IsNullOrWhiteSpace(v)) continue;
                LOGFONTW? lf = ParseCustomFont(v, r, key);
                if (lf == null) continue;
                if (key.Equals("IconFont", StringComparison.OrdinalIgnoreCase)) { iconTitle = lf; continue; }
                if (ncm == null) ncm = GetNonclientMetrics();
                if (ncm == null)
                {
                    r.Warnings.Add($"metrics: {key} skipped, there is no live NONCLIENTMETRICS to apply it to");
                    ok = false;
                    continue;
                }
                var n = ncm.Value;
                switch (key)
                {
                    case "CaptionFont": n.lfCaptionFont = lf.Value; break;
                    case "SmCaptionFont": n.lfSmCaptionFont = lf.Value; break;
                    case "MenuFont": n.lfMenuFont = lf.Value; break;
                    case "StatusFont": n.lfStatusFont = lf.Value; break;
                    case "MessageFont": n.lfMessageFont = lf.Value; break;
                }
                ncm = n;
            }

            if (ncm.HasValue)
            {
                var n = ncm.Value;
                n.cbSize = (uint)Marshal.SizeOf(typeof(NONCLIENTMETRICSW));
                if (!SystemParametersInfoW(SPI_SETNONCLIENTMETRICS, n.cbSize, ref n, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
                {
                    r.Warnings.Add($"metrics: SPI_SETNONCLIENTMETRICS failed ({Marshal.GetLastWin32Error()})");
                    ok = false;
                }
                using (var k = Registry.CurrentUser.CreateSubKey(WindowMetrics))
                {
                    k.SetValue("NonclientMetrics", MarshalBytes(n), RegistryValueKind.Binary);
                    SetFontValue(k, "CaptionFont", n.lfCaptionFont);
                    SetFontValue(k, "SmCaptionFont", n.lfSmCaptionFont);
                    SetFontValue(k, "MenuFont", n.lfMenuFont);
                    SetFontValue(k, "StatusFont", n.lfStatusFont);
                    SetFontValue(k, "MessageFont", n.lfMessageFont);
                }
                r.Notes.Add("metrics: .theme NonclientMetrics");
            }
            if (iconTitle.HasValue) ok &= ApplyIconTitleFont(iconTitle.Value, r);
            return ok;
        }

        /// <summary>
        /// xp's <c>[Metrics] CaptionFont</c> form (themeui/themefile.cpp:102-172 <c>_GetCustomFont</c>):
        /// <c>{Face, Npt, charset}</c>, optionally behind an <c>@dll,-id</c> MUI indirection. The point size becomes a
        /// 96-dpi height clamped to [-100, -3]; the charset is DEFAULT_CHARSET afterwards, as xp forces for every font
        /// it loads from a .theme (themefile.cpp:331-342). An unparsable value leaves the font alone.
        /// </summary>
        private static LOGFONTW? ParseCustomFont(string value, ThemeApplyResult r, string key)
        {
            string s = value.Trim();
            if (s.StartsWith("@", StringComparison.Ordinal))
            {
                var sb = new StringBuilder(512);
                if (SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) != 0)
                {
                    r.Warnings.Add($"metrics: {key}=\"{s}\" isn't a resource string this machine has; the font was left alone");
                    return null;
                }
                s = sb.ToString();
            }
            if (!s.StartsWith("{", StringComparison.Ordinal))
            {
                r.Warnings.Add($"metrics: {key}=\"{value}\" isn't a {{Face, Npt, charset}} descriptor; the font was left alone");
                return null;
            }

            var lf = new LOGFONTW { lfCharSet = 1 };
            string body = s.Substring(1);
            int close = body.IndexOf('}');
            if (close >= 0) body = body.Substring(0, close);
            var parts = body.Split(',');
            lf.lfFaceName = parts[0].Trim();
            if (lf.lfFaceName.Length > FontSpec.MaxFaceLength) lf.lfFaceName = lf.lfFaceName.Substring(0, FontSpec.MaxFaceLength);
            for (int i = 1; i < parts.Length; ++i)
            {
                string part = parts[i].Trim();
                if (part.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(part.Substring(0, part.Length - 2).Trim(), out int points)) continue;
                    int height = -MulDiv(points, 96, 72);
                    lf.lfHeight = -From96(Math.Max(3, Math.Min(100, -height)));
                }
                // the third field is the descriptor's charset; xp parses it and then overwrites every font with
                // DEFAULT_CHARSET anyway (themefile.cpp:331-342), so it is read and dropped here too.
            }
            if (lf.lfFaceName.Length == 0)
            {
                r.Warnings.Add($"metrics: {key}=\"{value}\" has no face name; the font was left alone");
                return null;
            }
            return lf;
        }

        private static byte[] ParseBytes(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var b = new byte[parts.Length];
            for (int i = 0; i < parts.Length; ++i)
                if (!byte.TryParse(parts[i], out b[i])) return null;
            return b;
        }

        /// <summary>xp stores the ANSI NONCLIENTMETRICS (cbSize 340, 60-byte LOGFONTA). win10 wants the wide struct
        /// (cbSize 504, 92-byte LOGFONTW, trailing iPaddedBorderWidth); only the face names change between them.
        /// <c>iPaddedBorderWidth</c> is left at 0 here and filled from the live value by the caller: the xp struct ends
        /// at <c>lfMessageFont</c>, so the blob has nothing to say about it.</summary>
        private static NONCLIENTMETRICSW? NonclientAnsiToWide(byte[] b)
        {
            if (!XpAnsiMetrics.IsNonclient(b)) return null;
            var n = new NONCLIENTMETRICSW { cbSize = 504 };
            n.iBorderWidth = From96(BitConverter.ToInt32(b, 4));
            n.iScrollWidth = From96(BitConverter.ToInt32(b, 8));
            n.iScrollHeight = From96(BitConverter.ToInt32(b, 12));
            n.iCaptionWidth = From96(BitConverter.ToInt32(b, 16));
            n.iCaptionHeight = From96(BitConverter.ToInt32(b, 20));
            n.lfCaptionFont = LogFontAnsiToWide(b, 24);
            n.iSmCaptionWidth = From96(BitConverter.ToInt32(b, 84));
            n.iSmCaptionHeight = From96(BitConverter.ToInt32(b, 88));
            n.lfSmCaptionFont = LogFontAnsiToWide(b, 92);
            n.iMenuWidth = From96(BitConverter.ToInt32(b, 152));
            n.iMenuHeight = From96(BitConverter.ToInt32(b, 156));
            n.lfMenuFont = LogFontAnsiToWide(b, 160);
            n.lfStatusFont = LogFontAnsiToWide(b, 220);
            n.lfMessageFont = LogFontAnsiToWide(b, 280);
            return n;
        }

        private static LOGFONTW LogFontAnsiToWide(byte[] b, int at)
        {
            // LOGFONTA: 5 longs, then italic/underline/strikeout/charset/outprec/clipprec/quality/pitch (bytes 20..27),
            // then lfFaceName[32] ANSI at 28. LOGFONTW is the same plus a 32-wchar name at 28 (92 bytes total).
            return new LOGFONTW
            {
                // the blob's heights are pixels at 96 dpi.
                lfHeight = ScaledHeight(BitConverter.ToInt32(b, at)),
                lfWidth = BitConverter.ToInt32(b, at + 4),
                lfEscapement = BitConverter.ToInt32(b, at + 8),
                lfOrientation = BitConverter.ToInt32(b, at + 12),
                lfWeight = BitConverter.ToInt32(b, at + 16),
                lfItalic = b[at + 20],
                lfUnderline = b[at + 21],
                lfStrikeOut = b[at + 22],
                // xp overrides the blob's charset with DEFAULT_CHARSET for every font it loads from a .theme: the shell
                // and NTUSER agreed to always use it and shim any app that tests the charset (themefile.cpp:331-342).
                // Classic.theme ships ANSI_CHARSET (0) on all six fonts, which makes GDI substitute a face.
                lfCharSet = 1,
                lfOutPrecision = b[at + 24],
                lfClipPrecision = b[at + 25],
                lfQuality = b[at + 26],
                lfPitchAndFamily = b[at + 27],
                lfFaceName = XpAnsiMetrics.FaceName(b, at),
            };
        }

        /// <summary>LOGFONT heights are magnitudes at 96 dpi in a theme blob: negative is a character height, positive a
        /// cell height.</summary>
        private static int ScaledHeight(int height)
        {
            return height < 0 ? -From96(-height) : From96(height);
        }

        /// <summary>the icon title font out of an xp ICONMETRICS blob (cbSize 76 + iHorzSpacing, iVertSpacing,
        /// iTitleWrap + a 60-byte LOGFONTA). The three ints are read past on purpose: xp keeps only <c>lfIconTitle</c>
        /// from this blob (themeui/themefile.cpp:243-259) and its own TODO still lists SPI_SETICONMETRICS as
        /// unimplemented, so the icon cell stays whatever <c>WindowMetrics</c> says.</summary>
        private static LOGFONTW? IconTitleFontAnsiToWide(byte[] b)
        {
            if (!XpAnsiMetrics.IsIcon(b)) return null;
            return LogFontAnsiToWide(b, XpAnsiMetrics.IconFontOffsets[0]);
        }

        /// <summary>
        /// The system colors. The style's SysMetrics are the base and the .theme's <c>[Control Panel\Colors]</c>
        /// overwrites them: xp seeds <c>schemeData</c> from the style and then replaces every entry the .theme names
        /// (themeui/themefile.cpp:190-232, :349-386). A .theme that ships colors is saying "these are the system
        /// colors", which is why <c>SZ_PBPROP_HASSYSMETRICS</c> requires <c>[Control Panel\Colors] ActiveTitle</c>
        /// (themefile.cpp:1265-1320). Two fallbacks come with it: a missing <c>GradientActiveTitle</c> is read from
        /// <c>ActiveTitle</c> and a missing <c>GradientInactiveTitle</c> from <c>InactiveTitle</c>
        /// (themefile.cpp:363-376), so a flat caption doesn't keep the previous theme's gradient.
        /// </summary>
        private static bool ApplyColors(List<KeyValuePair<int, int>> styleColors, ThemeIni ini, ThemeApplyResult r)
        {
            var colors = new int[XpThemeFiles.SysColorNames.Length];
            var known = new bool[colors.Length];
            foreach (var kv in styleColors)
            {
                if (kv.Key < 0 || kv.Key >= colors.Length) continue;
                colors[kv.Key] = kv.Value;
                known[kv.Key] = true;
            }

            int activeTitle = Array.IndexOf(XpThemeFiles.SysColorNames, "ActiveTitle");
            int inactiveTitle = Array.IndexOf(XpThemeFiles.SysColorNames, "InactiveTitle");
            int gradientActive = Array.IndexOf(XpThemeFiles.SysColorNames, "GradientActiveTitle");
            int gradientInactive = Array.IndexOf(XpThemeFiles.SysColorNames, "GradientInactiveTitle");
            for (int at = 0; at < colors.Length; ++at)
            {
                string v = ini.Get(@"Control Panel\Colors", XpThemeFiles.SysColorNames[at]);
                if (!string.IsNullOrWhiteSpace(v) && TryParseColor(v, out int parsed))
                {
                    colors[at] = parsed;
                    known[at] = true;
                    continue;
                }
                // the gradient captions follow the plain ones when the theme leaves them out (themefile.cpp:363-376).
                // xp reads SPI_GETGRADIENTCAPTIONS there but never tests it, so the fallback is unconditional.
                int source = at == gradientActive ? activeTitle : at == gradientInactive ? inactiveTitle : -1;
                if (source >= 0 && known[source]) { colors[at] = colors[source]; known[at] = true; }
            }

            var idx = new List<int>();
            var rgb = new List<int>();
            var persisted = new List<string>();
            using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Colors"))
            {
                for (int at = 0; at < colors.Length; ++at)
                {
                    if (!known[at]) continue;
                    int c = colors[at];
                    idx.Add(at);
                    rgb.Add(c);
                    key.SetValue(XpThemeFiles.SysColorNames[at], $"{c & 0xFF} {(c >> 8) & 0xFF} {(c >> 16) & 0xFF}", RegistryValueKind.String);
                    persisted.Add(XpThemeFiles.SysColorNames[at]);
                }
            }

            if (idx.Count == 0) { r.Warnings.Add("colors: neither the style's SysMetrics nor the theme carry system colors"); return false; }
            if (!SetSysColors(idx.Count, idx.ToArray(), rgb.ToArray()))
            {
                r.Warnings.Add("colors: SetSysColors failed");
                return false;
            }
            r.Notes.Add($"colors: {persisted.Count} system colors set and persisted");
            return true;
        }

        /// <summary>"R G B", xp's spelling in <c>[Control Panel\Colors]</c>; commas are tolerated.</summary>
        private static bool TryParseColor(string v, out int rgb)
        {
            rgb = 0;
            var parts = v.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!int.TryParse(parts[0], out int cr) || !int.TryParse(parts[1], out int cg) || !int.TryParse(parts[2], out int cb)) return false;
            rgb = cr | (cg << 8) | (cb << 16);
            return true;
        }

        /// <summary>
        /// The desktop picture and the screensaver. The screensaver goes to <c>SYSTEM.INI</c>'s <c>[boot]</c> section,
        /// not to the registry: xp's page writes <c>WritePrivateProfileString("boot", "SCRNSAVE.EXE", …, "system.ini")</c>
        /// with NULL (delete) when empty, stores the short path so apps peeking at the ini don't fault, and then calls
        /// <c>SPI_SETSCREENSAVEACTIVE</c> (themeui/screensaverpg.cpp:1725-1765, :2137-2205). The registry value is only a
        /// fallback source it reads back (:440-520), so writing it there is what xp never did.
        /// </summary>
        private static bool ApplyDesktop(ThemeIni ini, string themePath, ThemeApplyResult r)
        {
            string wallpaper = Expand(ini.Get(@"Control Panel\Desktop", "Wallpaper"), themePath);
            string screenSaver = Expand(ini.Get("boot", "SCRNSAVE.EXE"), themePath);
            bool ok = true;
            using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
            {
                if (!string.IsNullOrEmpty(ini.Get(@"Control Panel\Desktop", "WallpaperStyle")))
                    key.SetValue("WallpaperStyle", ini.Get(@"Control Panel\Desktop", "WallpaperStyle"), RegistryValueKind.String);
                if (!string.IsNullOrEmpty(ini.Get(@"Control Panel\Desktop", "TileWallpaper")))
                    key.SetValue("TileWallpaper", ini.Get(@"Control Panel\Desktop", "TileWallpaper"), RegistryValueKind.String);
                if (!string.IsNullOrEmpty(ini.Get(@"Control Panel\Desktop", "PicturePosition")))
                    key.SetValue("PicturePosition", ini.Get(@"Control Panel\Desktop", "PicturePosition"), RegistryValueKind.String);
                // only when the theme names a picture: writing "" clears the desktop for the next logon, and a
                // .theme that names no wallpaper says nothing about the user's own.
                if (!string.IsNullOrEmpty(wallpaper)) key.SetValue("Wallpaper", wallpaper, RegistryValueKind.String);
            }
            if (!string.IsNullOrEmpty(wallpaper) && File.Exists(wallpaper))
            {
                if (!SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, wallpaper, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
                {
                    r.Warnings.Add($"desktop: SPI_SETDESKWALLPAPER failed for {wallpaper}");
                    ok = false;
                }
                else
                    r.Notes.Add($"desktop: wallpaper {Path.GetFileName(wallpaper)}");
            }
            else if (!string.IsNullOrEmpty(wallpaper))
            {
                // the registry value was already written above; the picture just isn't there to draw. That is a
                // cosmetic miss, not a failed style application.
                r.Warnings.Add($"desktop: wallpaper \"{wallpaper}\" doesn't exist; the desktop picture was left alone");
            }

            if (!WriteSystemIniScreensaver(screenSaver, r)) ok = false;
            // only when the theme states it: a .theme without ScreenSaveActive says nothing about the user's screensaver,
            // and turning it off because the key is absent is not xp's behaviour (screensaverpg.cpp:1749-1756 writes the
            // ini value and then the active flag from the page's own checkbox).
            string activeText = ini.Get(@"Control Panel\Desktop", "ScreenSaveActive")?.Trim();
            if (activeText != null)
            {
                bool active = activeText == "1";
                if (!SystemParametersInfoW(SPI_SETSCREENSAVEACTIVE, active ? 1u : 0u, IntPtr.Zero, SPIF_UPDATEINIFILE))
                {
                    r.Warnings.Add($"desktop: SPI_SETSCREENSAVEACTIVE {(active ? "on" : "off")} failed ({Marshal.GetLastWin32Error()})");
                    ok = false;
                }
            }
            if (!string.IsNullOrEmpty(screenSaver)) r.Notes.Add($"desktop: screensaver {Path.GetFileName(screenSaver)}");
            return ok;
        }

        /// <summary>writes <c>SYSTEM.INI</c>'s <c>[boot] SCRNSAVE.EXE</c> when the theme names one, the same channel
        /// xp uses. A theme that names none leaves the user's screensaver alone: clearing it is desk.cpl's "None"
        /// selection, a user action, not something applying a visual style should do (and the write needs admin).
        /// A screensaver that isn't installed is reported and left alone, so the ini keeps naming something that
        /// exists.</summary>
        private static bool WriteSystemIniScreensaver(string screenSaver, ThemeApplyResult r)
        {
            if (string.IsNullOrWhiteSpace(screenSaver)) return true;
            string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "system.ini");
            try
            {
                if (!File.Exists(screenSaver))
                {
                    r.Warnings.Add($"desktop: screensaver \"{screenSaver}\" doesn't exist; SYSTEM.INI was left alone");
                    return true;
                }
                var shortPath = new StringBuilder(260);
                string value = GetShortPathName(screenSaver, shortPath, shortPath.Capacity) > 0 ? shortPath.ToString() : screenSaver;
                if (!WritePrivateProfileString("boot", "SCRNSAVE.EXE", value, ini))
                {
                    r.Warnings.Add($"desktop: could not write [boot] SCRNSAVE.EXE to {ini} ({Marshal.GetLastWin32Error()})");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                r.Warnings.Add($"desktop: could not write [boot] SCRNSAVE.EXE to {ini} ({ex.Message})");
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int cchBuffer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WritePrivateProfileString(string section, string key, string value, string file);

        private const uint SND_ASYNC = 0x0001;
        private const uint SND_NODEFAULT = 0x0002;

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool sndPlaySoundW(string sound, uint flags);

        /// <summary>
        /// The cursors. Only xp's own fifteen slots are touched (themeui/themefile.cpp:19 <c>s_pszCursorArray</c>), and
        /// a slot the theme doesn't name is <em>deleted</em> rather than written empty (:610 <c>HrRegDeleteValue</c>):
        /// the other eleven slots in <see cref="XpThemeFiles.CursorKeys"/> are win10 additions xp never had, so writing
        /// them blanks the user's own Person/Pin/Scroll* cursors on every apply.
        /// </summary>
        private static bool ApplyCursors(ThemeIni ini, string themePath, ThemeApplyResult r)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in XpThemeFiles.CursorKeys)
            {
                string v = Expand(ini.Get(@"Control Panel\Cursors", name), themePath);
                if (string.IsNullOrEmpty(v)) continue;
                // windows spells "Windows default" by leaving the slot empty; that is a slot value, not a file name.
                if (v.Equals("Windows default", StringComparison.OrdinalIgnoreCase) || v.StartsWith("@themeui.dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(v))
                {
                    r.Warnings.Add($"cursors: {name}=\"{v}\" isn't a file; that slot keeps the session's cursor");
                    continue;
                }
                values[name] = v;
            }
            string scheme = ini.Get(@"Control Panel\Cursors", "DefaultValue")?.Trim();
            bool windowsDefault = string.IsNullOrEmpty(scheme)
                || scheme.Equals("Windows default", StringComparison.OrdinalIgnoreCase)
                || scheme.StartsWith("@themeui.dll", StringComparison.OrdinalIgnoreCase);
            using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Cursors"))
            {
                // xp deletes a slot the theme doesn't name, which is what drops the previous pack's arrows; the value
                // type is REG_SZ, the one desk.cpl writes (the extras the porter used to write were REG_EXPAND_SZ).
                foreach (var name in XpCursorSlots)
                {
                    if (values.TryGetValue(name, out string v)) key.SetValue(name, v, RegistryValueKind.String);
                    else key.DeleteValue(name, false);
                }
                if (windowsDefault)
                {
                    // no scheme name and source 0: the session falls back to the windows defaults in HKLM's Schemes.
                    key.DeleteValue("", false);
                    key.SetValue("Scheme Source", 0, RegistryValueKind.DWord);
                }
                else
                {
                    key.SetValue("", scheme, RegistryValueKind.String);
                    // 1 = the scheme's entries are the per-user values written above (0 would make the session look the
                    // name up in HKLM's Schemes, where a pack's scheme never is). The scheme itself is registered too,
                    // in the 15-slot order Control Panel uses, so the name resolves after the switch instead of
                    // vanishing from the cursor list.
                    key.SetValue("Scheme Source", 1, RegistryValueKind.DWord);
                    string joined = string.Join(",", SchemeSlots.Select(n => values.TryGetValue(n, out string sv) ? sv : ""));
                    using (var schemes = Registry.CurrentUser.CreateSubKey(@"Control Panel\Cursors\Schemes"))
                        schemes.SetValue(scheme, joined, RegistryValueKind.ExpandString);
                }
            }
            // SPIF_UPDATEINIFILE makes SPI_SETCURSORS fail outright (measured: flags 0x3 and 0x1 both return FALSE with
            // GetLastError 0, flags 0 succeeds); the slots are already in the registry, so xp passes fWinIni 0 and only
            // the notify flag (themeui/themefile.cpp:639 SystemParametersInfoAsync(SPI_SETCURSORS, 0, 0, 0, SPIF_SENDCHANGE, NULL)).
            if (!SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDCHANGE))
            {
                r.Warnings.Add($"cursors: SPI_SETCURSORS failed ({Marshal.GetLastWin32Error()})");
                return false;
            }
            if (windowsDefault) r.Notes.Add("cursors: windows default cursors (scheme source 0)");
            else r.Notes.Add($"cursors: {values.Count} cursor slots set, scheme \"{scheme}\" (scheme source 1)");
            return true;
        }

        /// <summary>the events xp's theme engine applies, as fully qualified <c>.Current</c> keys
        /// (themeui/themefile.cpp:34-58 <c>s_ThemeSoundsValues</c>). The value lives <em>inside</em> the
        /// <c>.Current</c> section, which is what the porter's own generated .theme writes too.</summary>
        private static readonly string[] SoundEvents =
        {
            @"AppEvents\Schemes\Apps\.Default\.Default\.Current",
            @"AppEvents\Schemes\Apps\.Default\AppGPFault\.Current",
            @"AppEvents\Schemes\Apps\.Default\Close\.Current",
            @"AppEvents\Schemes\Apps\.Default\DeviceConnect\.Current",
            @"AppEvents\Schemes\Apps\.Default\DeviceDisconnect\.Current",
            @"AppEvents\Schemes\Apps\.Default\DeviceFail\.Current",
            @"AppEvents\Schemes\Apps\.Default\LowBatteryAlarm\.Current",
            @"AppEvents\Schemes\Apps\.Default\MailBeep\.Current",
            @"AppEvents\Schemes\Apps\.Default\Maximize\.Current",
            @"AppEvents\Schemes\Apps\.Default\MenuCommand\.Current",
            @"AppEvents\Schemes\Apps\.Default\MenuPopup\.Current",
            @"AppEvents\Schemes\Apps\.Default\Minimize\.Current",
            @"AppEvents\Schemes\Apps\.Default\Open\.Current",
            @"AppEvents\Schemes\Apps\.Default\PrintComplete\.Current",
            @"AppEvents\Schemes\Apps\.Default\RestoreDown\.Current",
            @"AppEvents\Schemes\Apps\.Default\RestoreUp\.Current",
            @"AppEvents\Schemes\Apps\.Default\RingIn\.Current",
            @"AppEvents\Schemes\Apps\.Default\Ringout\.Current",
            @"AppEvents\Schemes\Apps\.Default\SystemAsterisk\.Current",
            @"AppEvents\Schemes\Apps\.Default\SystemExclamation\.Current",
            @"AppEvents\Schemes\Apps\.Default\SystemExit\.Current",
            @"AppEvents\Schemes\Apps\.Default\SystemHand\.Current",
            @"AppEvents\Schemes\Apps\.Default\SystemNotification\.Current",
            @"AppEvents\Schemes\Apps\.Default\SystemQuestion\.Current",
            @"AppEvents\Schemes\Apps\.Default\SystemStart\.Current",
            @"AppEvents\Schemes\Apps\.Default\SystemStartMenu\.Current",
            @"AppEvents\Schemes\Apps\.Default\WindowsLogoff\.Current",
            @"AppEvents\Schemes\Apps\.Default\WindowsLogon\.Current",
            @"AppEvents\Schemes\Apps\Explorer\EmptyRecycleBin\.Current",
            @"AppEvents\Schemes\Apps\Explorer\Navigating\.Current",
        };

        /// <summary>the event whose "no value" state is the .Default scheme's own wav (xp's <c>SOUND_DEFAULT</c>).</summary>
        private const string DefaultSoundEvent = @"AppEvents\Schemes\Apps\.Default\.Default\.Current";

        /// <summary>
        /// The sound events. XP walks a fixed list of <c>.Current</c> keys, sets the ones the theme names, and for the
        /// rest <em>resets</em> the event: it deletes the value and, for the default event, copies the <c>.Default</c>
        /// scheme's wav in (themeui/themefile.cpp:520-578) - otherwise the previous pack's wav keeps playing. It then
        /// clears the scheme name to ".current" so the Mouse cpl doesn't show a bogus name (:583-585), which is the only
        /// thing it writes to <c>AppEvents\Schemes</c>.
        /// </summary>
        private static bool ApplySounds(ThemeIni ini, string themePath, ThemeApplyResult r)
        {
            int set = 0, reset = 0;
            bool ok = true;
            foreach (var key in SoundEvents)
            {
                // both xp's format and this porter's output spell it "DefaultValue"; the registry calls it "@".
                string v = ini.Get(key, "DefaultValue");
                if (string.IsNullOrWhiteSpace(v)) v = ini.Get(key, "@");
                v = Expand(v, themePath);
                if (string.IsNullOrEmpty(v) || !File.Exists(v))
                {
                    // the theme doesn't name this event: reset it instead of leaving the previous pack's wav.
                    try
                    {
                        using (var k = Registry.CurrentUser.CreateSubKey(key))
                        {
                            k.DeleteValue("", false);
                            if (key.Equals(DefaultSoundEvent, StringComparison.OrdinalIgnoreCase))
                            {
                                // the .Default scheme's own value for the event: xp swaps the trailing ".Current" for
                                // ".Default" (themefile.cpp:553-563), which is where the Sound cpl keeps the scheme's wav.
                                string fallback = ReadString(Registry.CurrentUser, key.Substring(0, key.Length - ".Current".Length) + @"\.Default", "");
                                if (!string.IsNullOrEmpty(fallback))
                                    k.SetValue("", fallback, fallback.IndexOf("%SystemRoot%", StringComparison.OrdinalIgnoreCase) >= 0 ? RegistryValueKind.ExpandString : RegistryValueKind.String);
                            }
                        }
                        ++reset;
                    }
                    catch (Exception ex) { r.Warnings.Add($"sounds: {key}: {ex.Message}"); ok = false; }
                    continue;
                }
                try
                {
                    // xp's type rule: REG_EXPAND_SZ only when the path still holds %SystemRoot% (themefile.cpp:563).
                    using (var k = Registry.CurrentUser.CreateSubKey(key))
                        k.SetValue("", v, v.IndexOf("%SystemRoot%", StringComparison.OrdinalIgnoreCase) >= 0 ? RegistryValueKind.ExpandString : RegistryValueKind.String);
                    ++set;
                }
                catch (Exception ex) { r.Warnings.Add($"sounds: {key}: {ex.Message}"); ok = false; }
            }

            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"AppEvents\Schemes"))
                    k.SetValue("", ".current", RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                r.Warnings.Add("sounds: could not clear the scheme name: " + ex.Message);
                ok = false;
            }

            // flush the sound cache so the next event plays the new wav (themefile.cpp:579).
            sndPlaySoundW(null, SND_ASYNC | SND_NODEFAULT);

            r.Notes.Add(set == 0
                ? $"sounds: no event sounds named by the theme or present in the pack; {reset} events reset to the default scheme"
                : $"sounds: {set} event sounds set, {reset} reset to the default scheme");
            return ok;
        }

        // per-user DefaultIcon values this porter wrote on a previous apply, so switching to a theme that doesn't name
        // that CLSID drops the old override instead of keeping the previous theme's icon (My Documents and the Recycle
        // Bin stayed on CrystalBlue's icons after every later theme).
        private const string IconOverrides = @"Software\xpThemePort\DefaultIcons";

        /// <summary>
        /// The per-CLSID <c>DefaultIcon</c> values. First the overrides a previous apply wrote are removed (so switching
        /// to a theme that doesn't name that CLSID drops the old icon), then the theme's own are written. A failure is
        /// reported per entry: an icon that silently didn't land is exactly what an empty catch used to hide.
        /// </summary>
        private static bool ApplyIcons(ThemeIni ini, string themePath, ThemeApplyResult r)
        {
            bool ok = true;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(IconOverrides, false))
                {
                    if (k != null)
                        foreach (var name in k.GetValueNames())
                        {
                            string v = k.GetValue(name) as string;
                            int bar = (v ?? "").IndexOf('|');
                            if (bar < 0) continue;
                            string iconKey = v.Substring(0, bar), value = v.Substring(bar + 1);
                            // IconOverrides used to record the pre-fix, wrong location (Explorer\CLSID\{...} itself, whose
                            // default value is the shell's display-name override). Clean both locations so switching themes
                            // also repairs names those old applies overwrote with icon paths.
                            // The root key's default value is also where the user renames "This PC": only remove it while it
                            // still holds the icon path this porter wrote, never a name the user typed afterwards.
                            foreach (var suffix in new[] { "", @"\DefaultIcon" })
                                try
                                {
                                    using (var t = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\" + iconKey + suffix, true))
                                    {
                                        if (t == null) continue;
                                        if (suffix.Length == 0 && !string.Equals(t.GetValue(value) as string, v, StringComparison.OrdinalIgnoreCase)) continue;
                                        t.DeleteValue(value, false);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    r.Warnings.Add($"icons: could not drop the previous override {iconKey}\\{value} ({ex.Message})");
                                    ok = false;
                                }
                        }
                    Registry.CurrentUser.DeleteSubKeyTree(IconOverrides, false);
                }
            }
            catch (Exception ex)
            {
                r.Warnings.Add($"icons: could not read the previous overrides ({ex.Message}); some may stay in place");
                ok = false;
            }
            var written = new List<string>();
            int set = 0;
            foreach (var section in ini.Sections)
            {
                if (!section.Key.EndsWith(@"}\DefaultIcon", StringComparison.OrdinalIgnoreCase)) continue;
                // The theme's section is Explorer\CLSID\{...}\DefaultIcon. Dropping "\DefaultIcon" and writing the value
                // on Explorer\CLSID\{...} itself overwrites that key's default value, which the shell uses as the special
                // icon's display name: Computer/Network/UsersFiles then showed "C:\Windows\System32\imageres.dll,-109"
                // as their labels. Keep the \DefaultIcon subkey.
                string iconKey = section.Key.Substring(0, section.Key.Length - @"\DefaultIcon".Length);
                foreach (var kv in section.Value.Keys)
                {
                    string v = Expand(kv.Value, themePath);
                    if (string.IsNullOrEmpty(v) || v.StartsWith("@", StringComparison.Ordinal)) continue;
                    if (!File.Exists(v.Split(',')[0])) continue;
                    string name = kv.Key.Equals("DefaultValue", StringComparison.OrdinalIgnoreCase) ? "" : kv.Key;
                    try
                    {
                        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\" + iconKey + @"\DefaultIcon"))
                        {
                            k.SetValue(name, v, RegistryValueKind.String);
                            if (!string.Equals(k.GetValue(name) as string, v, StringComparison.Ordinal))
                                throw new IOException("icon registry read-back did not match");
                        }
                        written.Add(iconKey + "|" + name);
                        ++set;
                    }
                    catch (Exception ex)
                    {
                        r.Warnings.Add($"icons: {iconKey}\\{name} could not be set ({ex.Message})");
                        ok = false;
                    }
                }
            }
            if (written.Count > 0)
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(IconOverrides))
                        for (int i = 0; i < written.Count; ++i) k.SetValue("v" + i, written[i], RegistryValueKind.String);
                }
                catch (Exception ex)
                {
                    r.Warnings.Add($"icons: could not record the overrides for the next theme switch ({ex.Message})");
                    ok = false;
                }
            if (set == 0) r.Notes.Add("icons: no default icons to set");
            else
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, 0x3000 /* SHCNF_FLUSHNOWAIT */, IntPtr.Zero, IntPtr.Zero);
                r.Notes.Add($"icons: {set} default icons set and read back; shell refresh queued");
            }
            return ok;
        }

        private static byte[] MarshalBytes<T>(T value) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            var buf = new byte[size];
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, p, false);
                Marshal.Copy(p, buf, 0, size);
            }
            finally { Marshal.FreeHGlobal(p); }
            return buf;
        }

        /// <summary>xps's theme tokens with uxtheme's semantics (themefile.cpp ReplaceStringToken): %WinDir%,
        /// %SystemRoot%, %ThemeDir% and %ResourceDir% are substituted and a path separator is inserted when the text
        /// after the token doesn't start with one, because xp writes "%WinDir%explorer.exe" and "%WinDir%resources\...".
        /// %ThemeDir% is the .theme's own folder, not the style's. Anything else goes through the environment, and a
        /// path outside %WinDir% is kept absolute rather than being bent into a %WinDir% spelling.</summary>
        private static string Expand(string v, string themePath)
        {
            if (string.IsNullOrWhiteSpace(v)) return v ?? "";
            string s = v.Trim().Trim('"');
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            s = ReplaceToken(s, "%WinDir%", win);
            s = ReplaceToken(s, "%SystemRoot%", win);
            s = ReplaceToken(s, "%ResourceDir%", Path.Combine(win, "Resources"));
            string themeDir = ThemeDirectory(themePath);
            if (themeDir != null) s = ReplaceToken(s, "%ThemeDir%", themeDir);
            return ExpandEnvironment(s);
        }

        /// <summary>the .theme's folder, or null when the path can't be resolved (the token then stays in place rather
        /// than the whole value failing).</summary>
        private static string ThemeDirectory(string themePath)
        {
            if (string.IsNullOrEmpty(themePath)) return null;
            try { return Path.GetDirectoryName(Path.GetFullPath(themePath)); }
            catch (ArgumentException) { return null; }
            catch (PathTooLongException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        /// <summary>%VAR% substitution; a value whose variables can't be expanded is kept as written.</summary>
        private static string ExpandEnvironment(string s)
        {
            try { return Environment.ExpandEnvironmentVariables(s); }
            catch (ArgumentException) { return s; }
        }

        private static string ReplaceToken(string s, string token, string value)
        {
            if (string.IsNullOrEmpty(value)) return s;
            int at = s.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                string rest = s.Substring(at + token.Length);
                bool separator = rest.Length > 0 && rest[0] != '\\' && rest[0] != '/'
                    && !value.EndsWith("\\", StringComparison.Ordinal) && !value.EndsWith("/", StringComparison.Ordinal);
                s = s.Substring(0, at) + value + (separator ? "\\" : "") + rest;
                at = s.IndexOf(token, Math.Min(s.Length, at + value.Length + 1), StringComparison.OrdinalIgnoreCase);
            }
            return s;
        }

        private static string ReadString(RegistryKey root, string subKey, string name)
        {
            try
            {
                using (var k = root.OpenSubKey(subKey))
                    return k?.GetValue(name) as string;
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// The xp <c>[Metrics]</c> blobs: <c>NONCLIENTMETRICSA</c> (cbSize 340) and <c>ICONMETRICSA</c> (cbSize 76), both
    /// holding 60-byte <c>LOGFONTA</c> records whose face name is 32 ANSI bytes at +28. The only record of that layout:
    /// the applier decodes it to apply the fonts and the importer walks it to report the faces, so a change here moves
    /// both. A blob whose declared cbSize disagrees with its length is not one of these (themeui checks the same pair,
    /// themefile.cpp:243-259).
    /// </summary>
    internal static class XpAnsiMetrics
    {
        public const int NonclientSize = 340;
        public const int IconSize = 76;
        /// <summary>bytes per LOGFONTA (5 longs + 8 bytes of flags + a 32-byte name).</summary>
        public const int LogFontSize = 60;
        /// <summary>the face name inside a LOGFONTA.</summary>
        public const int FaceOffset = 28;
        public const int FaceLength = 32;

        public static readonly int[] NonclientFontOffsets = { 24, 92, 160, 220, 280 };
        public static readonly int[] IconFontOffsets = { 16 };

        public static bool IsNonclient(byte[] b)
        {
            return b != null && b.Length >= NonclientSize && BitConverter.ToUInt32(b, 0) == NonclientSize;
        }

        public static bool IsIcon(byte[] b)
        {
            return b != null && b.Length >= IconSize && BitConverter.ToUInt32(b, 0) == IconSize;
        }

        /// <summary>the LOGFONTA offsets of the named blob ("NonclientMetrics" / "IconMetrics"), null when the blob
        /// isn't the xp layout.</summary>
        public static int[] FontOffsets(string metricsKey, byte[] b)
        {
            if (metricsKey == null) return null;
            if (metricsKey.Equals("NonclientMetrics", StringComparison.OrdinalIgnoreCase))
                return IsNonclient(b) ? NonclientFontOffsets : null;
            if (metricsKey.Equals("IconMetrics", StringComparison.OrdinalIgnoreCase))
                return IsIcon(b) ? IconFontOffsets : null;
            return null;
        }

        /// <summary>the ANSI face name of the LOGFONTA at <paramref name="at"/>, empty when the record doesn't fit.</summary>
        public static string FaceName(byte[] b, int at)
        {
            if (b == null || at < 0 || at + LogFontSize > b.Length) return "";
            int len = Array.IndexOf(b, (byte)0, at + FaceOffset, FaceLength);
            len = len < 0 ? FaceLength : len - (at + FaceOffset);
            return Encoding.Default.GetString(b, at + FaceOffset, len).Trim();
        }
    }
}
