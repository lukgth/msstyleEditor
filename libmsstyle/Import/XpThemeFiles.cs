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
            var files = Directory.GetFiles(dir, "*.theme");
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

        private static Desktop ReadThemeFile(string path)
        {
            var d = new Desktop { ThemePath = path };
            string section = "";
            foreach (var raw in File.ReadAllLines(path))
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
            // xp theme packs spell it as an absolute path that assumed the machine the pack was made on.
            string expanded = Environment.ExpandEnvironmentVariables(v.Replace("%ResourceDir%", "%SystemRoot%"));
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
            return null;
        }


        /// <summary>[Control Panel\Colors] key names, in sysmetrics order (TMT_SCROLLBAR 1601 .. TMT_MENUBAR 1631).</summary>
        private static readonly string[] ColorNames =
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
                if (p.Header.typeID == (int)IDENTIFIER.COLOR && i >= 0 && i < ColorNames.Length && p.GetValue() is Color c)
                    d[ColorNames[i]] = c;
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
        public static string BuildTheme(string displayName, string styleFolder, string styleFile, string wallpaper, IDictionary<string, Color> colors = null,
            string colorStyle = "NormalColor", Desktop fit = null)
        {
            foreach (var v in new[] { displayName, styleFolder, styleFile, wallpaper, colorStyle })
            {
                if (string.IsNullOrEmpty(v) || v.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new ArgumentException("theme values must be single, non-empty lines");
            }
            // xp's WallpaperStyle/ TileWallpaper carried over into the pair win10 reads (Win10 stores the same idea
            // under WallpaperStyle + PicturePosition). null fit = this generator's default, fill.
            bool tile = fit?.TileWallpaper ?? false;
            int wallStyle = fit == null ? 10 : tile ? 0 : fit.WallpaperStyle == 2 ? 2 : 0;
            int picturePosition = fit == null ? 2 : tile ? 3 : fit.WallpaperStyle == 2 ? 2 : 4;
            var sb = new StringBuilder();
            void L(string s = "") => sb.Append(s).Append("\r\n");
            L("; Generated by msstyleImport import-xp");
            L();
            L("[Theme]");
            L("DisplayName=" + displayName);
            L("SetLogonBackground=0");
            L();
            L("; Computer - SHIDI_SERVER");
            L(@"[CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\DefaultIcon]");
            L(@"DefaultValue=%SystemRoot%\System32\imageres.dll,-109");
            L();
            L("; UsersFiles - SHIDI_USERFILES");
            L(@"[CLSID\{59031A47-3F72-44A7-89C5-5595FE6B30EE}\DefaultIcon]");
            L(@"DefaultValue=%SystemRoot%\System32\imageres.dll,-123");
            L();
            L("; Network - SHIDI_MYNETWORK");
            L(@"[CLSID\{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}\DefaultIcon]");
            L(@"DefaultValue=%SystemRoot%\System32\imageres.dll,-25");
            L();
            L("; Recycle Bin - SHIDI_RECYCLERFULL SHIDI_RECYCLER");
            L(@"[CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\DefaultIcon]");
            L(@"Full=%SystemRoot%\System32\imageres.dll,-54");
            L(@"Empty=%SystemRoot%\System32\imageres.dll,-55");
            L();
            L(@"[Control Panel\Cursors]");
            foreach (var c in new[] { "AppStarting", "Arrow", "Crosshair", "Hand", "Help", "IBeam", "No", "NWPen", "SizeAll", "SizeNESW", "SizeNS", "SizeNWSE", "SizeWE", "UpArrow", "Wait" })
                L(c + "=");
            L("DefaultValue=Windows Default");
            L("DefaultValue.MUI=@themeui.dll,-2043");
            L();
            if (colors != null && colors.Count > 0)
            {
                L(@"[Control Panel\Colors]");
                foreach (var name in ColorNames.Where(colors.ContainsKey))
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
            L("[VisualStyles]");
            L($@"Path=%ResourceDir%\Themes\{styleFolder}\{styleFile}");
            L("ColorStyle=" + colorStyle);
            L("Size=NormalSize");
            L("AutoColorization=0");
            L("ColorizationColor=0XC40078D7");
            L("AppMode=Light");
            L("SystemMode=Dark");
            L("VisualStyleVersion=10");
            L();
            L("[boot]");
            L("SCRNSAVE.EXE=");
            L();
            L("[MasterThemeSelector]");
            L("MTSM=RJSPBS");
            L();
            L("[Sounds]");
            L("; IDS_SCHEME_DEFAULT");
            L(@"SchemeName=@%SystemRoot%\System32\mmres.dll,-800");
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
