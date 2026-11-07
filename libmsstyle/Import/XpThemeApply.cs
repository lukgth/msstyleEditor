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
        private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref ICONMETRICSW pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref LOGFONTW pvParam, uint fWinIni);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPI_SETICONTITLELOGFONT = 0x0022;
        private const uint SPI_GETNONCLIENTMETRICS = 0x0029;
        private const uint SPI_SETNONCLIENTMETRICS = 0x002A;
        private const uint SPI_SETICONMETRICS = 0x002E;
        private const uint SPI_SETCURSORS = 0x0057;
        private const uint SPI_SETFLATMENU = 0x1023;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;

        private const int SHCNE_ASSOCCHANGED = 0x08000000;

        private const string WindowMetrics = @"Control Panel\Desktop\WindowMetrics";

        /// <summary>system color names in TMT/COLOR_* order (TMT_SCROLLBAR 1601 .. TMT_MENUBAR 1631).</summary>
        private static readonly string[] ColorNames =
        {
            "Scrollbar", "Background", "ActiveTitle", "InactiveTitle", "Menu", "Window", "WindowFrame", "MenuText",
            "WindowText", "TitleText", "ActiveBorder", "InactiveBorder", "AppWorkspace", "Hilight", "HilightText",
            "ButtonFace", "ButtonShadow", "GrayText", "ButtonText", "InactiveTitleText", "ButtonHilight",
            "ButtonDkShadow", "ButtonLight", "InfoText", "InfoWindow", "ButtonAlternateFace", "HotTrackingColor",
            "GradientActiveTitle", "GradientInactiveTitle", "MenuHilight", "MenuBar",
        };

        private static readonly string[] CursorNames =
        {
            "Arrow", "Help", "AppStarting", "Wait", "NWPen", "No", "Crosshair", "IBeam", "UpArrow", "Hand", "Link",
            "SizeAll", "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeVertical", "SizeHorizontal", "Person", "Pin",
            "ScrollWE", "ScrollNS", "ScrollNW", "ScrollNE", "ScrollSE", "ScrollSW",
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

        /// <summary>SysMetrics fonts (TMT 801..806) -> the NONCLIENTMETRICS field / WindowMetrics value name.</summary>
        private static readonly (int Id, string Name)[] FontMetrics =
        {
            (801, "CaptionFont"),
            (802, "SmCaptionFont"),
            (803, "MenuFont"),
            (804, "StatusFont"),
            (805, "MessageFont"),
            (806, "IconFont"),
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
        /// The icon metrics Windows 10 accepts: cbSize, the icon cell's width and height (what SM_CXICONSPACING
        /// reports), iTitleWrap, LOGFONTW. There is no separate spacing pair in this layout: putting the .theme's
        /// spacing numbers here (as the old struct did with iHorzSpacing first) shifted every field by 8 bytes and the
        /// shell took the first two as cell width/height (107px cells for CrystalBlue instead of xp's 75).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ICONMETRICSW
        {
            public uint cbSize;
            public int iWidth, iHeight, iTitleWrap;
            public LOGFONTW lfFont;
        }

        /// <summary>
        /// Applies <paramref name="themePath"/> (a .theme installed under %WINDIR%\Resources\Themes) without opening any
        /// Windows UI. Returns notes for each section and Ok only when the visual style itself was accepted by uxtheme.
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
            uint hr = SetSystemVisualStyle(stylePath, color, size, 0);
            if (hr != 0)
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
            //    they broadcast can make the shell re-read colors.
            var ncm = GetNonclientMetrics();
            var sm = ReadStyleMetrics(stylePath, ref ncm);
            if (sm.NcmModified) ApplyAndPersistNcm(ncm, sm);
            if (sm.IconTitleFont.HasValue) ApplyIconTitleFont(sm.IconTitleFont.Value, r);

            // 3. the .theme's own [Metrics] blobs: an xp pack's theme can override the style's numbers, and the blobs are
            //    the same shape ("Windows Classic.theme" of the working ports uses them).
            ApplyThemeMetrics(ini, r);

            // 4. colors last, from the applied style's SysMetrics (what themeui does) and falling back to the .theme's
            //    [Control Panel\Colors] when the style has none.
            ApplyColors(sm.Colors, ini, r);

            // 5. desktop: picture, fit, screensaver.
            ApplyDesktop(ini, r);

            // 6. cursors, sound events and per-CLSID default icons (shell settings, not style records).
            ApplyCursors(ini, r);
            ApplySounds(ini, r);
            ApplyIcons(ini, r);

            // 7. current theme, so desk.cpl and Personalization name what is applied.
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes"))
                    k.SetValue("CurrentTheme", themePath, RegistryValueKind.String);
            }
            catch (Exception ex) { r.Warnings.Add("theme: could not write Themes\\CurrentTheme: " + ex.Message); }

            r.CurrentTheme = ReadString(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes", "CurrentTheme");
            r.DllName = Expand(ReadString(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ThemeManager", "DllName"), themePath);
            r.Notes.Add($"current: {r.CurrentTheme}");
            r.Notes.Add($"style in use: {r.DllName}");
            return r;
        }

        private sealed class StyleMetrics
        {
            public List<KeyValuePair<int, int>> Colors { get; } = new List<KeyValuePair<int, int>>();
            public bool NcmModified;
            public LOGFONTW? IconTitleFont;
        }

        /// <summary>reads the applied style's "sysmetrics" class: system colors, the ten sizes, the six fonts and flat menus.
        /// Same source themeui uses; the SemanticModel is not available here, the raw class is what the OS reads too.</summary>
        private static StyleMetrics ReadStyleMetrics(string stylePath, ref NONCLIENTMETRICSW ncm)
        {
            var sm = new StyleMetrics();
            try
            {
                var style = new VisualStyle();
                style.Load(stylePath);
                using (style)
                {
                    StyleClass cls = null;
                    foreach (var c in style.Classes.Values)
                        if (c.ClassName.Equals("sysmetrics", StringComparison.OrdinalIgnoreCase)) { cls = c; break; }
                    if (cls == null) return sm;

                    foreach (var part in cls.Parts.Values)
                        foreach (var state in part.States.Values)
                            foreach (var prop in state.Properties)
                                ReadSystemMetric(prop, style.PreferredStringTable, ref ncm, sm);
                }
            }
            catch (Exception) { }
            return sm;
        }

        private static void ReadSystemMetric(StyleProperty prop, Dictionary<int, string> stringTable, ref NONCLIENTMETRICSW ncm, StyleMetrics sm)
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
                switch ((IDENTIFIER)id)
                {
                    case IDENTIFIER.SIZINGBORDERWIDTH: ncm.iBorderWidth = size; sm.NcmModified = true; break;
                    case IDENTIFIER.SCROLLBARWIDTH: ncm.iScrollWidth = size; sm.NcmModified = true; break;
                    case IDENTIFIER.SCROLLBARHEIGHT: ncm.iScrollHeight = size; sm.NcmModified = true; break;
                    case IDENTIFIER.CAPTIONBARWIDTH: ncm.iCaptionWidth = size; sm.NcmModified = true; break;
                    case IDENTIFIER.CAPTIONBARHEIGHT: ncm.iCaptionHeight = size; sm.NcmModified = true; break;
                    case IDENTIFIER.SMCAPTIONBARWIDTH: ncm.iSmCaptionWidth = size; sm.NcmModified = true; break;
                    case IDENTIFIER.SMCAPTIONBARHEIGHT: ncm.iSmCaptionHeight = size; sm.NcmModified = true; break;
                    case IDENTIFIER.MENUBARWIDTH: ncm.iMenuWidth = size; sm.NcmModified = true; break;
                    case IDENTIFIER.MENUBARHEIGHT: ncm.iMenuHeight = size; sm.NcmModified = true; break;
                    case IDENTIFIER.PADDEDBORDERWIDTH: ncm.iPaddedBorderWidth = size; sm.NcmModified = true; break;
                }
                return;
            }

            if (id >= (int)IDENTIFIER.FIRSTFONT && id <= (int)IDENTIFIER.ICONTITLEFONT && type == IDENTIFIER.FONT)
            {
                if (stringTable == null || !stringTable.TryGetValue(prop.Header.shortFlag, out string text)) return;
                LOGFONTW lf = ParseFontString(text);
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

            if (id == (int)IDENTIFIER.FLATMENUS && type == IDENTIFIER.BOOLTYPE && prop.GetValue() is bool flat)
                SystemParametersInfoW(SPI_SETFLATMENU, flat ? 1u : 0u, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }

        /// <summary>the style's font resource is "Name, Size, [bold], [italic], [underline], [quality:Name|N]" (themeui's
        /// own spelling); the size is points, LOGFONT wants pixels at the current DPI.</summary>
        private static LOGFONTW ParseFontString(string text)
        {
            var lf = new LOGFONTW { lfWeight = 400, lfCharSet = 1, lfQuality = 5 };
            var parts = text.Split(',');
            if (parts.Length >= 1) lf.lfFaceName = parts[0].Trim();
            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int points))
                lf.lfHeight = -(points * 96 / 72);
            for (int i = 2; i < parts.Length; ++i)
            {
                switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "bold": lf.lfWeight = 700; break;
                    case "italic": lf.lfItalic = 1; break;
                    case "underline": lf.lfUnderline = 1; break;
                    case "strikeout": lf.lfStrikeOut = 1; break;
                }
            }
            return lf;
        }

        private static NONCLIENTMETRICSW GetNonclientMetrics()
        {
            var ncm = new NONCLIENTMETRICSW { cbSize = (uint)Marshal.SizeOf(typeof(NONCLIENTMETRICSW)) };
            if (!SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, ncm.cbSize, ref ncm, 0))
                ncm.cbSize = (uint)Marshal.SizeOf(typeof(NONCLIENTMETRICSW));
            return ncm;
        }

        private static void ApplyAndPersistNcm(NONCLIENTMETRICSW ncm, StyleMetrics sm)
        {
            ncm.cbSize = (uint)Marshal.SizeOf(typeof(NONCLIENTMETRICSW));
            SystemParametersInfoW(SPI_SETNONCLIENTMETRICS, ncm.cbSize, ref ncm, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

            using (var k = Registry.CurrentUser.CreateSubKey(WindowMetrics))
            {
                // the registry keeps sizes as a signed pixel count in twips (15 per pixel), negative for the sizes that
                // are subtracted from the client area; PaddedBorderWidth is the one positive.
                foreach (var m in SizeMetrics)
                {
                    int v = GetAlloc(GetNcmSize(ref ncm, m.Id));
                    k.SetValue(m.Name, (m.Negative ? -v : v * 15).ToString(), RegistryValueKind.String);
                }
                k.SetValue("NonclientMetrics", MarshalBytes(ncm), RegistryValueKind.Binary);
                SetFontValue(k, "CaptionFont", ncm.lfCaptionFont);
                SetFontValue(k, "SmCaptionFont", ncm.lfSmCaptionFont);
                SetFontValue(k, "MenuFont", ncm.lfMenuFont);
                SetFontValue(k, "StatusFont", ncm.lfStatusFont);
                SetFontValue(k, "MessageFont", ncm.lfMessageFont);
            }
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

        private static int GetAlloc(int px)
        {
            return px * 15;
        }

        private static void SetFontValue(RegistryKey k, string name, LOGFONTW lf)
        {
            if (string.IsNullOrEmpty(lf.lfFaceName)) return;
            k.SetValue(name, MarshalBytes(lf), RegistryValueKind.Binary);
        }

        private static void ApplyIconTitleFont(LOGFONTW lf, ThemeApplyResult r)
        {
            SystemParametersInfoW(SPI_SETICONTITLELOGFONT, (uint)Marshal.SizeOf(typeof(LOGFONTW)), ref lf, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            using (var k = Registry.CurrentUser.CreateSubKey(WindowMetrics))
                SetFontValue(k, "IconFont", lf);
            r.Notes.Add($"sysmetrics: icon title font {lf.lfFaceName}");
        }

        private static void ApplyThemeMetrics(ThemeIni ini, ThemeApplyResult r)
        {
            string nc = ini.Get("Metrics", "NonclientMetrics");
            string ic = ini.Get("Metrics", "IconMetrics");
            if (string.IsNullOrEmpty(nc) && string.IsNullOrEmpty(ic)) return;

            using (var k = Registry.CurrentUser.CreateSubKey(WindowMetrics))
            {
                if (!string.IsNullOrEmpty(nc))
                {
                    var wide = NonclientAnsiToWide(ParseBytes(nc));
                    if (wide == null) r.Warnings.Add("metrics: NonclientMetrics isn't the xp ANSI blob (cbSize 340); skipped");
                    else
                    {
                        var ncm = wide.Value;
                        ncm.cbSize = (uint)Marshal.SizeOf(typeof(NONCLIENTMETRICSW));
                        SystemParametersInfoW(SPI_SETNONCLIENTMETRICS, ncm.cbSize, ref ncm, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                        k.SetValue("NonclientMetrics", MarshalBytes(ncm), RegistryValueKind.Binary);
                        r.Notes.Add("metrics: .theme NonclientMetrics");
                    }
                }
                if (!string.IsNullOrEmpty(ic))
                {
                    var wide = IconMetricsAnsiToWide(ParseBytes(ic));
                    if (wide == null) r.Warnings.Add("metrics: IconMetrics isn't the xp ANSI blob (cbSize 76); skipped");
                    else
                    {
                        var im = wide.Value;
                        im.cbSize = (uint)Marshal.SizeOf(typeof(ICONMETRICSW));
                        SystemParametersInfoW(SPI_SETICONMETRICS, im.cbSize, ref im, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                        k.SetValue("IconMetrics", MarshalBytes(im), RegistryValueKind.Binary);
                        r.Notes.Add("metrics: .theme IconMetrics");
                    }
                }
            }
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
        /// (cbSize 504, 92-byte LOGFONTW, trailing iPaddedBorderWidth); only the face names change between them.</summary>
        private static NONCLIENTMETRICSW? NonclientAnsiToWide(byte[] b)
        {
            if (b == null || b.Length < 340 || BitConverter.ToUInt32(b, 0) != 340) return null;
            var n = new NONCLIENTMETRICSW { cbSize = 504 };
            n.iBorderWidth = BitConverter.ToInt32(b, 4);
            n.iScrollWidth = BitConverter.ToInt32(b, 8);
            n.iScrollHeight = BitConverter.ToInt32(b, 12);
            n.iCaptionWidth = BitConverter.ToInt32(b, 16);
            n.iCaptionHeight = BitConverter.ToInt32(b, 20);
            n.lfCaptionFont = LogFontAnsiToWide(b, 24);
            n.iSmCaptionWidth = BitConverter.ToInt32(b, 84);
            n.iSmCaptionHeight = BitConverter.ToInt32(b, 88);
            n.lfSmCaptionFont = LogFontAnsiToWide(b, 92);
            n.iMenuWidth = BitConverter.ToInt32(b, 152);
            n.iMenuHeight = BitConverter.ToInt32(b, 156);
            n.lfMenuFont = LogFontAnsiToWide(b, 160);
            n.lfStatusFont = LogFontAnsiToWide(b, 220);
            n.lfMessageFont = LogFontAnsiToWide(b, 280);
            n.iPaddedBorderWidth = 0;
            return n;
        }

        private static LOGFONTW LogFontAnsiToWide(byte[] b, int at)
        {
            // LOGFONTA: 5 longs, then italic/underline/strikeout/charset/outprec/clipprec/quality/pitch (bytes 20..27),
            // then lfFaceName[32] ANSI at 28. LOGFONTW is the same plus a 32-wchar name at 28 (92 bytes total).
            return new LOGFONTW
            {
                lfHeight = BitConverter.ToInt32(b, at),
                lfWidth = BitConverter.ToInt32(b, at + 4),
                lfEscapement = BitConverter.ToInt32(b, at + 8),
                lfOrientation = BitConverter.ToInt32(b, at + 12),
                lfWeight = BitConverter.ToInt32(b, at + 16),
                lfItalic = b[at + 20],
                lfUnderline = b[at + 21],
                lfStrikeOut = b[at + 22],
                lfCharSet = b[at + 23],
                lfOutPrecision = b[at + 24],
                lfClipPrecision = b[at + 25],
                lfQuality = b[at + 26],
                lfPitchAndFamily = b[at + 27],
                lfFaceName = AnsiName(b, at + 28),
            };
        }

        private static string AnsiName(byte[] b, int at)
        {
            var name = new byte[32];
            Array.Copy(b, at, name, 0, 32);
            int len = Array.IndexOf(name, (byte)0);
            if (len < 0) len = 32;
            return Encoding.Default.GetString(name, 0, len);
        }

        /// <summary>xp ICONMETRICS: cbSize 76 + three ints + 60-byte LOGFONTA; win10 wants cbSize 108 + 92-byte LOGFONTW.</summary>
        private static ICONMETRICSW? IconMetricsAnsiToWide(byte[] b)
        {
            if (b == null || b.Length < 76 || BitConverter.ToUInt32(b, 0) != 76) return null;
            // xp's theme engine reads only the icon title font out of the .theme's IconMetrics (themeui/themefile.cpp:
            // m_systemMetrics.schemeData.lfIconTitle = iconMetricsW.lfFont); the blob's numbers are not the desktop's
            // icon spacing. xp's default cell is 75px (IconSpacing -1125 twips), so that is what goes in; taking the
            // blob's numbers made win10 open 107px cells.
            return new ICONMETRICSW
            {
                cbSize = 108,
                iWidth = 75,
                iHeight = 75,
                iTitleWrap = 1,
                lfFont = LogFontAnsiToWide(b, 16),
            };
        }

        private static void ApplyColors(List<KeyValuePair<int, int>> styleColors, ThemeIni ini, ThemeApplyResult r)
        {
            var idx = new List<int>();
            var rgb = new List<int>();
            var persisted = new List<string>();

            using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Colors"))
            {
                if (styleColors.Count > 0)
                {
                    foreach (var kv in styleColors)
                    {
                        if (kv.Key < 0 || kv.Key >= ColorNames.Length) continue;
                        idx.Add(kv.Key);
                        rgb.Add(kv.Value);
                        key.SetValue(ColorNames[kv.Key], $"{kv.Value & 0xFF} {(kv.Value >> 8) & 0xFF} {(kv.Value >> 16) & 0xFF}", RegistryValueKind.String);
                        persisted.Add(ColorNames[kv.Key]);
                    }
                }
                else
                {
                    // no sysmetrics colors in the style: fall back to the .theme's own [Control Panel\Colors] lines.
                    for (int at = 0; at < ColorNames.Length; ++at)
                    {
                        string v = ini.Get(@"Control Panel\Colors", ColorNames[at]);
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        var parts = v.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3 || !int.TryParse(parts[0], out int cr) || !int.TryParse(parts[1], out int cg) || !int.TryParse(parts[2], out int cb)) continue;
                        idx.Add(at); rgb.Add(cr | (cg << 8) | (cb << 16));
                        key.SetValue(ColorNames[at], $"{cr} {cg} {cb}", RegistryValueKind.String);
                        persisted.Add(ColorNames[at]);
                    }
                }
            }

            if (idx.Count == 0) { r.Warnings.Add("colors: neither the style's SysMetrics nor the theme carry system colors"); return; }
            if (!SetSysColors(idx.Count, idx.ToArray(), rgb.ToArray()))
                r.Warnings.Add("colors: SetSysColors failed");
            r.Notes.Add($"colors: {persisted.Count} system colors set and persisted");
        }

        private static void ApplyDesktop(ThemeIni ini, ThemeApplyResult r)
        {
            string wallpaper = ExceptionSafeExpand(ini.Get(@"Control Panel\Desktop", "Wallpaper"));
            string screenSaver = ExceptionSafeExpand(ini.Get("boot", "SCRNSAVE.EXE"));
            using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
            {
                if (!string.IsNullOrEmpty(ini.Get(@"Control Panel\Desktop", "WallpaperStyle")))
                    key.SetValue("WallpaperStyle", ini.Get(@"Control Panel\Desktop", "WallpaperStyle"), RegistryValueKind.String);
                if (!string.IsNullOrEmpty(ini.Get(@"Control Panel\Desktop", "TileWallpaper")))
                    key.SetValue("TileWallpaper", ini.Get(@"Control Panel\Desktop", "TileWallpaper"), RegistryValueKind.String);
                if (!string.IsNullOrEmpty(ini.Get(@"Control Panel\Desktop", "PicturePosition")))
                    key.SetValue("PicturePosition", ini.Get(@"Control Panel\Desktop", "PicturePosition"), RegistryValueKind.String);
                key.SetValue("Wallpaper", wallpaper ?? "", RegistryValueKind.String);
                if (!string.IsNullOrEmpty(screenSaver)) key.SetValue("SCRNSAVE.EXE", screenSaver, RegistryValueKind.String);
            }
            if (!string.IsNullOrEmpty(wallpaper) && File.Exists(wallpaper))
            {
                if (!SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, wallpaper, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
                    r.Warnings.Add($"desktop: SPI_SETDESKWALLPAPER failed for {wallpaper}");
                else
                    r.Notes.Add($"desktop: wallpaper {Path.GetFileName(wallpaper)}");
            }
            else if (!string.IsNullOrEmpty(wallpaper))
                r.Warnings.Add($"desktop: wallpaper \"{wallpaper}\" doesn't exist; the desktop picture was left alone");
            if (!string.IsNullOrEmpty(screenSaver)) r.Notes.Add($"desktop: screensaver {Path.GetFileName(screenSaver)}");
        }

        private static void ApplyCursors(ThemeIni ini, ThemeApplyResult r)
        {
            var values = new List<string>();
            using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Cursors"))
            {
                foreach (var name in CursorNames)
                {
                    string v = ExceptionSafeExpand(ini.Get(@"Control Panel\Cursors", name));
                    if (string.IsNullOrEmpty(v) || !File.Exists(v)) continue;
                    key.SetValue(name, v, RegistryValueKind.ExpandString);
                    values.Add(name);
                }
                string scheme = ini.Get(@"Control Panel\Cursors", "DefaultValue");
                if (!string.IsNullOrEmpty(scheme) && !scheme.StartsWith("@", StringComparison.Ordinal))
                    key.SetValue("", scheme, RegistryValueKind.String);
            }
            if (values.Count == 0) { r.Notes.Add("cursors: the theme names no cursor files; the session's are kept"); return; }
            if (!SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0))
                r.Warnings.Add("cursors: SPI_SETCURSORS failed");
            else
                r.Notes.Add($"cursors: {values.Count} cursor slots set");
        }

        private static void ApplySounds(ThemeIni ini, ThemeApplyResult r)
        {
            int set = 0;
            foreach (var section in ini.Sections)
            {
                if (!section.Key.StartsWith(@"AppEvents\Schemes\Apps\", StringComparison.OrdinalIgnoreCase)) continue;
                string v = ExceptionSafeExpand(ini.Get(section.Key, "DefaultValue"));
                if (string.IsNullOrEmpty(v) || !File.Exists(v)) continue;
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(section.Key))
                        k.SetValue("", v, RegistryValueKind.ExpandString);
                    ++set;
                }
                catch (Exception) { }
            }
            if (set == 0) r.Notes.Add("sounds: no event sounds named by the theme or present in the pack");
            else r.Notes.Add($"sounds: {set} event sounds set");
        }

        // per-user DefaultIcon values this porter wrote on a previous apply, so switching to a theme that doesn't name
        // that CLSID drops the old override instead of keeping the previous theme's icon (My Documents and the Recycle
        // Bin stayed on CrystalBlue's icons after every later theme).
        private const string IconOverrides = @"Software\xpThemePort\DefaultIcons";

        private static void ApplyIcons(ThemeIni ini, ThemeApplyResult r)
        {
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
                            using (var t = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\" + v.Substring(0, bar), true))
                                if (t != null) t.DeleteValue(v.Substring(bar + 1), false);
                        }
                    Registry.CurrentUser.DeleteSubKeyTree(IconOverrides, false);
                }
            }
            catch (Exception) { }
            var written = new List<string>();
            int set = 0;
            foreach (var section in ini.Sections)
            {
                if (!section.Key.EndsWith(@"}\DefaultIcon", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var kv in section.Value.Keys)
                {
                    string v = ExceptionSafeExpand(kv.Value);
                    if (string.IsNullOrEmpty(v) || v.StartsWith("@", StringComparison.Ordinal)) continue;
                    if (!File.Exists(v.Split(',')[0])) continue;
                    string name = kv.Key.Equals("DefaultValue", StringComparison.OrdinalIgnoreCase) ? "" : kv.Key;
                    try
                    {
                        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\" + section.Key))
                            k.SetValue(name, v, RegistryValueKind.String);
                        written.Add(section.Key + "|" + name);
                        ++set;
                    }
                    catch (Exception) { }
                }
            }
            if (written.Count > 0)
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(IconOverrides))
                        for (int i = 0; i < written.Count; ++i) k.SetValue("v" + i, written[i], RegistryValueKind.String);
                }
                catch (Exception) { }
            if (set == 0) r.Notes.Add("icons: no default icons to set");
            else
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
                r.Notes.Add($"icons: {set} default icons set");
            }
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

        private static string ExceptionSafeExpand(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return v;
            try { return Environment.ExpandEnvironmentVariables(v.Trim().Trim('"')); }
            catch (Exception) { return v; }
        }

        /// <summary>theme paths use %ResourceDir% (only VisualStyles understands it) plus the usual environment names.</summary>
        private static string Expand(string v, string themePath)
        {
            if (string.IsNullOrWhiteSpace(v)) return v;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string s = v.Trim().Trim('"').Replace("%ResourceDir%", Path.Combine(win, "Resources"));
            return Environment.ExpandEnvironmentVariables(s);
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
}
