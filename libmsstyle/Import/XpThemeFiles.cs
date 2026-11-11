using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace libmsstyle.Import
{
    /// <summary>
    /// The .theme file that goes with an imported style. Same layout as the working ports in
    /// ported_to_10_already (Royale.theme): the style is expected to be installed as
    /// %ResourceDir%\Themes\&lt;folder&gt;\&lt;file&gt;.msstyles and the .theme next to that folder.
    /// </summary>
    public static class XpThemeFiles
    {
        public const string DefaultWallpaper = @"%WinDir%\Web\Wallpaper\Bliss.bmp";

        /// <summary>
        /// The desktop section of an xp theme file: which picture xp showed and how it fitted.
        /// <see cref="WallpaperPath"/> is the resolved file when it exists next to or beside the theme file.
        /// </summary>
        public class Desktop
        {
            /// <summary>the .theme the values came from, null when the source ships none.</summary>
            public string ThemePath { get; set; }
            /// <summary>[VisualStyles] ColorStyle of that file.</summary>
            public string ColorStyle { get; set; }
            /// <summary>the raw Wallpaper value from that file.</summary>
            public string Wallpaper { get; set; }
            /// <summary>xp: 0 centered, 1 tiled, 2 stretched.</summary>
            public int WallpaperStyle { get; set; }
            public bool TileWallpaper { get; set; }
            /// <summary>the wallpaper file, resolved through %windir%, the theme's own folder and the style's folder.</summary>
            public string WallpaperPath { get; set; }
        }

        /// <summary>
        /// Reads the desktop section xp shipped for a color scheme: the .theme files beside the .msstyles, the one whose
        /// [VisualStyles] ColorStyle is the scheme's (xp theme packs name them per scheme, xanadu01..06). A theme file that
        /// names a wallpaper wins over one that doesn't, so a pack whose first file carries no picture still ports it.
        /// </summary>
        public static Desktop ReadDesktop(string sourceStylePath, string colorStyle)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(sourceStylePath));
            if (!Directory.Exists(dir)) return null;
            // the pack's .theme often sits one level above the .msstyles ("GloWorm\GloWorm.theme" next to "GloWorm\GloWorm\").
            var dirs = new[] { dir, Path.GetDirectoryName(dir) }.Where(d => d != null && Directory.Exists(d)).ToList();
            var files = dirs.SelectMany(d => Directory.GetFiles(d, "*.theme")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) return null;
            Desktop best = null;
            int bestRank = -1;
            foreach (var f in files)
            {
                var d = ReadThemeFile(f);
                if (d == null) continue;
                bool sameScheme = string.Equals(d.ColorStyle, colorStyle, StringComparison.OrdinalIgnoreCase);
                bool hasWallpaper = !string.IsNullOrEmpty(d.Wallpaper);
                int rank = (sameScheme ? 2 : 0) + (hasWallpaper ? 1 : 0);
                if (rank <= bestRank) continue;
                bestRank = rank;
                best = d;
            }
            if (best == null) return null;
            best.WallpaperPath = ResolveWallpaper(best.Wallpaper, best.ThemePath, dir);
            return best;
        }

        /// <summary>
        /// The lines of an .theme, decoded the way xp reads one: a BOM decides, and without one the file is ANSI in the
        /// system codepage (xp's <c>SHSetIniStringW</c> writes plain ANSI and only adds a UTF-7 <c>[Section.W]</c> shadow
        /// for values that aren't 7-bit clean, shlwapi/inistr.cpp:249-290). <see cref="File.ReadAllLines(string)"/>
        /// assumes UTF-8 with a replacement fallback, which turns a German pack's
        /// <c>C:\Dokumente und Einstellungen\Müller\…</c> into U+FFFD and makes every path test fail.
        /// </summary>
        internal static string[] ReadThemeLines(string path)
        {
            return File.ReadAllLines(path, DetectThemeEncoding(path));
        }

        /// <summary>the encoding a BOM names (this porter's own output is UTF-8 with one), the ANSI codepage otherwise.</summary>
        private static Encoding DetectThemeEncoding(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int b0 = fs.ReadByte(), b1 = fs.ReadByte(), b2 = fs.ReadByte();
                    if (b0 == 0xEF && b1 == 0xBB && b2 == 0xBF) return Encoding.UTF8;
                    if (b0 == 0xFF && b1 == 0xFE) return Encoding.Unicode;
                    if (b0 == 0xFE && b1 == 0xFF) return Encoding.BigEndianUnicode;
                    return Encoding.Default;
                }
            }
            catch (IOException) { return Encoding.Default; }
            catch (UnauthorizedAccessException) { return Encoding.Default; }
        }

        private static Desktop ReadThemeFile(string path)
        {
            var d = new Desktop { ThemePath = path };
            string section = "";
            foreach (var raw in ReadThemeLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.IndexOf(']') > 0)
                {
                    section = line.Substring(1, line.IndexOf(']') - 1).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (section.Equals(@"Control Panel\Desktop", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "wallpaper": d.Wallpaper = value; break;
                        case "wallpaperstyle": if (int.TryParse(value, out int ws)) d.WallpaperStyle = ws; break;
                        case "tilewallpaper": d.TileWallpaper = value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
                else if (section.Equals("VisualStyles", StringComparison.OrdinalIgnoreCase) && key.Equals("ColorStyle", StringComparison.OrdinalIgnoreCase))
                {
                    d.ColorStyle = value;
                }
            }
            return d;
        }

        private static string ResolveWallpaper(string raw, string themePath, string styleDir)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string v = raw.Trim().Trim('"');
            if (v.Length == 0) return null;
            // xp theme packs spell it as an absolute path that assumed the machine the pack was made on; %ThemeDir% is
            // the .theme's folder, and xp omits the separator after %WinDir% ("%WinDir%resources\Themes\...").
            string themeDir = themePath == null ? null : Path.GetDirectoryName(Path.GetFullPath(themePath));
            string expanded = XpThemeExtras.ExpandTokens(v, themeDir);
            var candidates = new List<string>();
            if (Path.IsPathRooted(expanded)) candidates.Add(expanded);
            string styleParent = Path.GetDirectoryName(styleDir);
            candidates.Add(Path.Combine(styleDir, Path.GetFileName(v)));
            candidates.Add(Path.Combine(styleDir, "Wallpaper", Path.GetFileName(v)));
            candidates.Add(Path.Combine(Path.GetDirectoryName(themePath) ?? styleDir, Path.GetFileName(v)));
            // packs often keep the picture in the folder above the .msstyles ("xanadu_vs\xanadu.jpg", the style in
            // "xanadu_vs\Xanadu\"); xp itself read it from C:\WINDOWS because the pack's readme put it there.
            if (styleParent != null)
            {
                candidates.Add(Path.Combine(styleParent, Path.GetFileName(v)));
                candidates.Add(Path.Combine(styleParent, "Wallpaper", Path.GetFileName(v)));
            }
            foreach (var c in candidates)
                if (File.Exists(c)) return Path.GetFullPath(c);
            // last resort: the pack may keep the picture deeper in its tree (Beige names wallpaper_69.jpg from
            // %ResourceDir%\Themes\ but ships nothing by that name, so this usually stays empty).
            foreach (var root in new[] { styleDir, styleParent })
            {
                if (root == null || !Directory.Exists(root)) continue;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, Path.GetFileName(v), SearchOption.AllDirectories))
                        return Path.GetFullPath(f);
                }
                // a subfolder the walk can't read is skipped: the picture may still be found under another root.
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
            }
            return null;
        }


        /// <summary>[Control Panel\Cursors] slot names, in the order xp's theme files spell them
        /// (themeui/themefile.cpp:19 s_pszCursorArray plus the win10-only slots a pack may still fill).</summary>
        internal static readonly string[] CursorKeys =
        {
            "Arrow", "Help", "AppStarting", "Wait", "NWPen", "No", "Crosshair", "IBeam", "UpArrow", "Hand", "Link",
            "SizeAll", "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeVertical", "SizeHorizontal", "Person", "Pin",
            "ScrollWE", "ScrollNS", "ScrollNW", "ScrollNE", "ScrollSE", "ScrollSW",
        };

        /// <summary>the six <c>[Metrics]</c> font descriptors xp reads on top of the two blobs
        /// (themeui/themefile.cpp:174-185 <c>_LoadCustomFonts</c>), in the order it reads them. Each is a
        /// <c>{Face, Npt, charset}</c> string, sometimes behind an <c>@dll,-id</c> indirection.</summary>
        internal static readonly string[] MetricFontKeys =
        {
            "CaptionFont", "SmCaptionFont", "MenuFont", "StatusFont", "MessageFont", "IconFont",
        };

        /// <summary>[Control Panel\Colors] key names, in sysmetrics order (TMT_SCROLLBAR 1601 .. TMT_MENUBAR 1631).
        /// The applier writes the same table, so this is the only copy.</summary>
        internal static readonly string[] SysColorNames =
        {
            "Scrollbar", "Background", "ActiveTitle", "InactiveTitle", "Menu", "Window", "WindowFrame", "MenuText",
            "WindowText", "TitleText", "ActiveBorder", "InactiveBorder", "AppWorkspace", "Hilight", "HilightText",
            "ButtonFace", "ButtonShadow", "GrayText", "ButtonText", "InactiveTitleText", "ButtonHilight",
            "ButtonDkShadow", "ButtonLight", "InfoText", "InfoWindow", "ButtonAlternateFace", "HotTrackingColor",
            "GradientActiveTitle", "GradientInactiveTitle", "MenuHilight", "MenuBar",
        };

        /// <summary>
        /// The style's sysmetrics colors as [Control Panel\Colors] lines. Win10 doesn't load a visual style's
        /// system colors into win32k on apply, so without these a port runs with the previous theme's
        /// WINDOW/WINDOWTEXT/MENUTEXT: appows' list text came out in GSM's light blue on its light window.
        /// </summary>
        public static IDictionary<string, Color> SysColors(VisualStyle style)
        {
            var d = new Dictionary<string, Color>();
            var sm = style.Classes.Values.FirstOrDefault(c => string.Equals(c.ClassName, "sysmetrics", StringComparison.OrdinalIgnoreCase));
            if (sm == null || !sm.Parts.TryGetValue(0, out var part) || !part.States.TryGetValue(0, out var state)) return d;
            foreach (var p in state.Properties)
            {
                int i = p.Header.nameID - (int)IDENTIFIER.FIRSTCOLOR;
                if (p.Header.typeID == (int)IDENTIFIER.COLOR && i >= 0 && i < SysColorNames.Length && p.GetValue() is Color c)
                    d[SysColorNames[i]] = c;
            }
            return d;
        }

        /// <param name="displayName">name shown in the theme picker.</param>
        /// <param name="styleFolder">folder name under %ResourceDir%\Themes.</param>
        /// <param name="styleFile">file name of the .msstyles inside that folder.</param>
        /// <param name="wallpaper">value for [Control Panel\Desktop] Wallpaper (environment variables allowed).</param>
        /// <param name="colors">[Control Panel\Colors] entries (<see cref="SysColors"/>); null or empty writes no section.</param>
        /// <param name="colorStyle">the style's color variant name ([VisualStyles] ColorStyle; windows loads Shell\&lt;it&gt;).</param>
        /// <param name="fit">how xp fitted that picture; null keeps this generator's default (fill).</param>
        /// <param name="extras">system metrics, cursors, icons, screensaver, sounds from the xp pack; null = none.</param>
        /// <param name="sizeName">the written style's normal size variant name (VMAP size column), so [VisualStyles] Size
        /// resolves; xp packs call it NormalSize, but a template may name it differently.</param>
        public static string BuildTheme(string displayName, string styleFolder, string styleFile, string wallpaper, IDictionary<string, Color> colors = null,
            string colorStyle = "NormalColor", Desktop fit = null, XpThemeExtras extras = null, string sizeName = "NormalSize")
        {
            foreach (var v in new[] { displayName, styleFolder, styleFile, wallpaper, colorStyle })
            {
                if (string.IsNullOrEmpty(v) || v.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new ArgumentException("theme values must be single, non-empty lines");
            }
            // xp's three states (theme.idl BKDGT_CENTER/TILE/STRECH) map onto win10's WallpaperStyle, which is the value
            // that decides: 0 centered, 2 stretched, 10 fill. TileWallpaper=1 forces tiling whatever WallpaperStyle says.
            // PicturePosition is inert on win10, so it is written as the matching DWPOS number instead of a second,
            // contradictory claim: a centered picture said "fill" (4) and a tiled one "fit" (3) next to a
            // WallpaperStyle that said otherwise. null fit = this generator's default, fill.
            bool tile = fit?.TileWallpaper ?? false;
            int wallStyle = fit == null ? 10 : tile ? 0 : fit.WallpaperStyle == 2 ? 2 : 0;
            int picturePosition = tile ? 1 : wallStyle == 2 ? 2 : wallStyle == 10 ? 4 : 0;
            var iconOverrides = new Dictionary<string, List<(string Key, string Value)>>(StringComparer.OrdinalIgnoreCase);
            if (extras != null)
                foreach (var i in extras.Icons)
                {
                    string key = Canon(i.Section);
                    if (!iconOverrides.TryGetValue(key, out var list)) iconOverrides[key] = list = new List<(string, string)>();
                    list.Add((i.Key, i.Value));
                }
            string Canon(string section) => (section ?? "").Trim().Trim('[', ']');
            var sb = new StringBuilder();
            void L(string s = "") => sb.Append(s).Append("\r\n");
            L("; Generated by msstyleImport import-xp");
            L();
            L("[Theme]");
            L("DisplayName=" + displayName);
            L("SetLogonBackground=0");
            L();
            // Only resolved source icons are emitted; never replace missing XP artwork with Win10 imageres icons.
            foreach (var icon in iconOverrides)
            {
                L("[" + icon.Key + "]");
                foreach (var entry in icon.Value) L(entry.Key + "=" + entry.Value);
                L();
            }

            L(@"[Control Panel\Cursors]");
            if (extras != null && extras.Cursors.Count > 0)
            {
                foreach (var c in extras.Cursors) L(c.Key + "=" + c.Value);
                L("DefaultValue=" + (extras.CursorSchemeName ?? "Windows Default"));
                if (extras.CursorSchemeName == null || extras.CursorSchemeName == "Windows Default")
                    L("DefaultValue.MUI=@themeui.dll,-2043");
            }
            else
            {
                // the same slot list the applier writes, so a pack that ships cursors and one that doesn't produce the
                // same section (the old 15-key list was a subset of the 26 the reader knows).
                foreach (var c in CursorKeys) L(c + "=");
                L("DefaultValue=Windows Default");
                L("DefaultValue.MUI=@themeui.dll,-2043");
            }
            L();
            if (colors != null && colors.Count > 0)
            {
                L(@"[Control Panel\Colors]");
                foreach (var name in SysColorNames.Where(colors.ContainsKey))
                    L($"{name}={colors[name].R} {colors[name].G} {colors[name].B}");
                L();
            }
            L(@"[Control Panel\Desktop]");
            L("Wallpaper=" + wallpaper);
            L("TileWallpaper=" + (tile ? "1" : "0"));
            L("WallpaperStyle=" + wallStyle);
            L("PicturePosition=" + picturePosition);
            L("Pattern=");
            L();
            // [Metrics]: the two blobs plus the six font descriptors. The descriptors are read straight from the source
            // .theme because a pack that names its fonts that way (classic.theme's "@themeui.dll,-2037"…"-2042") has
            // nothing else to carry them: dropping them loses the fonts the style's own records don't mention.
            var metricLines = new List<(string Key, string Value)>();
            if (extras != null) metricLines.AddRange(extras.Metrics.Select(m => (m.Key, m.Value)));
            if (fit?.ThemePath != null)
            {
                var source = ThemeIni.Read(fit.ThemePath);
                foreach (var key in MetricFontKeys)
                {
                    string v = source.Get("Metrics", key);
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (metricLines.Any(l => l.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                    metricLines.Add((key, v.Trim()));
                }
            }
            if (metricLines.Count > 0)
            {
                L("[Metrics]");
                foreach (var m in metricLines) L(m.Key + "=" + m.Value);
                L();
            }
            L("[VisualStyles]");
            L($@"Path=%ResourceDir%\Themes\{styleFolder}\{styleFile}");
            L("ColorStyle=" + colorStyle);
            L("Size=" + (string.IsNullOrWhiteSpace(sizeName) ? "NormalSize" : sizeName.Trim()));
            L("AutoColorization=0");
            L("ColorizationColor=0XC40078D7");
            L("AppMode=Light");
            L("SystemMode=Light");
            L("VisualStyleVersion=10");
            L();
            L("[boot]");
            L("SCRNSAVE.EXE=" + (extras?.Screensaver ?? ""));
            L();
            L("[MasterThemeSelector]");
            L("MTSM=RJSPBS");
            L();
            L("[Sounds]");
            L("; IDS_SCHEME_DEFAULT");
            L(@"SchemeName=@%SystemRoot%\System32\mmres.dll,-800");
            if (extras != null)
            {
                foreach (var s in extras.Sounds)
                {
                    L();
                    L("[" + s.Section + "]");
                    L("@=" + s.Value);
                }
            }
            return sb.ToString();
        }

        public static void WriteTheme(string path, string content)
        {
            File.WriteAllText(path, content, new UTF8Encoding(true));
        }

        /// <summary>file name safe for a folder / .msstyles name: invalid characters become '_'.</summary>
        public static string SafeName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.Trim())
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.Length == 0 ? "Imported" : sb.ToString();
        }
    }
}
