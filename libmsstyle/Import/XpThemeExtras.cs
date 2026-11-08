using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace libmsstyle.Import
{
    /// <summary>
    /// The parts of an xp .theme beyond colors and the desktop picture that still mean something on Windows 10:
    /// system metric blobs, custom cursors, desktop icons, the screensaver and the sound scheme. Pictures, cursors,
    /// sounds and icon files the pack shipped are copied into the style folder and every path is rewritten to
    /// %WinDir%\Resources\Themes\&lt;folder&gt;, because the packs spell paths as where they were installed
    /// ("%WinDir%Resources\Themes\MRSSilver\Cursors\cross.cur") and Windows 10 doesn't keep them there.
    /// </summary>
    public class XpThemeExtras
    {
        /// <summary>key = value lines for [Metrics], verbatim: the xp theme stores ANSI NONCLIENTMETRICS/ICONMETRICS
        /// blobs, which is the same shape the working Windows 10 ports write (Windows Classic.theme).</summary>
        public List<KeyValuePair<string, string>> Metrics { get; } = new List<KeyValuePair<string, string>>();
        /// <summary>[CLSID\...\DefaultIcon] lines: (section, key, value), value already rewritten.</summary>
        public List<(string Section, string Key, string Value)> Icons { get; } = new List<(string, string, string)>();
        /// <summary>[Control Panel\Cursors] lines in xp's key order, value already rewritten / empty.</summary>
        public List<KeyValuePair<string, string>> Cursors { get; } = new List<KeyValuePair<string, string>>();
        /// <summary>the cursor scheme's display name, null = Windows Default.</summary>
        public string CursorSchemeName { get; set; }
        /// <summary>[AppEvents\...] lines: (section, value), value already rewritten.</summary>
        public List<(string Section, string Value)> Sounds { get; } = new List<(string, string)>();
        /// <summary>[boot] SCRNSAVE.EXE value, already rewritten or null.</summary>
        public string Screensaver { get; set; }

        public List<string> Notes { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        private static readonly string[] CursorKeys =
        {
            "Arrow", "Help", "AppStarting", "Wait", "NWPen", "No", "Crosshair", "IBeam", "UpArrow", "Hand", "Link",
            "SizeAll", "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeVertical", "SizeHorizontal", "Person", "Pin",
            "ScrollWE", "ScrollNS", "ScrollNW", "ScrollNE", "ScrollSE", "ScrollSW",
        };

        private static readonly string[] MetricsKeys = { "IconMetrics", "NonclientMetrics" };

        /// <summary>
        /// Reads the extra sections of the .theme the desktop section came from. <paramref name="styleFolder"/> is the
        /// folder name the style is installed under; copied files land in &lt;styleDir&gt; and the emitted paths point
        /// at %WinDir%\Resources\Themes\&lt;styleFolder&gt;.
        /// </summary>
        public static XpThemeExtras Read(XpThemeFiles.Desktop desktop, string sourceDir, string styleDir, string styleFolder)
        {
            var ex = new XpThemeExtras();
            if (desktop?.ThemePath == null) return ex;
            var ini = ThemeIni.Read(desktop.ThemePath);

            foreach (var key in MetricsKeys)
            {
                string v = ini.Get("Metrics", key);
                if (string.IsNullOrWhiteSpace(v)) continue;
                ex.Metrics.Add(new KeyValuePair<string, string>(key, v.Trim()));
            }
            if (ex.Metrics.Count > 0)
                ex.Notes.Add($"metrics: {(ex.Metrics.Count == 2 ? "icon and nonclient" : ex.Metrics[0].Key)} metrics taken from {Path.GetFileName(desktop.ThemePath)}");

            // [CLSID\...\DefaultIcon]: only entries whose picture the pack shipped. The rest are xp icon ids
            // (shell32.dll,32) that name a different icon on Windows 10, or the pack's own nail of the win10 defaults.
            foreach (var kvp in ini.Sections)
            {
                var section = kvp.Value;
                if (!section.Name.EndsWith(@"}\DefaultIcon", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var kv in section.Keys)
                {
                    string resolved = Resolve(section.Name, kv.Value, sourceDir, styleDir, styleFolder, "Icons", ex.Warnings, out string why, packOnly: true);
                    if (resolved == null) continue;
                    ex.Icons.Add((section.Name, kv.Key, resolved));
                }
            }
            if (ex.Icons.Count > 0) ex.Notes.Add($"icons: {ex.Icons.Count} DefaultIcon entr{(ex.Icons.Count == 1 ? "y" : "ies")} use pictures the pack shipped");

            // [Control Panel\Cursors]
            string scheme = ini.Get(@"Control Panel\Cursors", "DefaultValue");
            var cursorValues = new List<KeyValuePair<string, string>>();
            foreach (var key in CursorKeys)
            {
                string v = ini.Get(@"Control Panel\Cursors", key);
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (v.Trim().Equals("Windows default", StringComparison.OrdinalIgnoreCase) || v.Trim().StartsWith("@themeui.dll", StringComparison.OrdinalIgnoreCase))
                {
                    cursorValues.Add(new KeyValuePair<string, string>(key, ""));
                    continue;
                }
                string resolved = Resolve(@"Control Panel\Cursors", v, sourceDir, styleDir, styleFolder, "Cursors", ex.Warnings, out string why, packOnly: true);
                cursorValues.Add(new KeyValuePair<string, string>(key, resolved ?? ""));
            }
            if (cursorValues.Any(kv => kv.Value.Length > 0))
            {
                ex.Cursors.AddRange(cursorValues);
                if (!string.IsNullOrWhiteSpace(scheme) && !scheme.Trim().Equals("Windows default", StringComparison.OrdinalIgnoreCase)
                    && !scheme.Trim().StartsWith("@themeui.dll", StringComparison.OrdinalIgnoreCase))
                    ex.CursorSchemeName = scheme.Trim();
                else
                    ex.CursorSchemeName = "Windows Default";
                ex.Notes.Add($"cursors: {cursorValues.Count(kv => kv.Value.Length > 0)} cursor files from the pack, scheme \"{ex.CursorSchemeName}\"");
            }

            // [boot] SCRNSAVE.EXE
            string screensaver = ini.Get("boot", "SCRNSAVE.EXE");
            if (!string.IsNullOrWhiteSpace(screensaver))
            {
                string resolved = Resolve("boot", screensaver, sourceDir, styleDir, styleFolder, "Screensavers", ex.Warnings, out string why);
                if (resolved != null)
                {
                    ex.Screensaver = resolved;
                    ex.Notes.Add($"screensaver: {Path.GetFileName(resolved)}");
                }
                else
                    ex.Notes.Add("screensaver: xp's SCRNSAVE.EXE isn't a file this machine has or the pack shipped; left empty");
            }

            // [AppEvents\...] sound scheme: only the events whose wav the pack shipped or this machine has.
            foreach (var section in ini.Sections.Where(s => s.Key.StartsWith(@"appevents\schemes\apps\", StringComparison.OrdinalIgnoreCase)).Select(s => s.Value))
            {
                // xp's theme files spell the event's default value "DefaultValue"; the registry calls it "@".
                string v = ini.Get(section.Name, "DefaultValue") ?? ini.Get(section.Name, "@");
                if (string.IsNullOrWhiteSpace(v)) continue;
                string resolved = Resolve(section.Name, v, sourceDir, styleDir, styleFolder, "Sounds", ex.Warnings, out string why);
                if (resolved == null) continue;
                ex.Sounds.Add((section.Name, resolved));
            }
            if (ex.Sounds.Count > 0) ex.Notes.Add($"sounds: {ex.Sounds.Count} events use wavs the pack shipped");

            return ex;
        }

        /// <summary>
        /// Turns a packed xp path into one Windows 10 can load. System files that exist are kept as they are; otherwise
        /// the file name is looked up inside the pack and copied into &lt;styleDir&gt;\&lt;subdir&gt;. Icon values keep their
        /// ",index" tail.
        /// </summary>
        private static string Resolve(string section, string raw, string sourceDir, string styleDir, string styleFolder,
            string subdir, List<string> warnings, out string why, bool packOnly = false)
        {
            why = null;
            string v = raw.Trim().Trim('"');
            if (v.Length == 0) return null;
            string index = null;
            int comma = v.LastIndexOf(',');
            if (comma > 0 && int.TryParse(v.Substring(comma + 1).Trim(), out _))
            {
                index = v.Substring(comma + 1).Trim();
                v = v.Substring(0, comma).Trim();
            }
            string name = Path.GetFileName(v.Replace('/', '\\'));
            if (name.Length == 0) { why = "no file name"; return null; }
            string expanded = Environment.ExpandEnvironmentVariables(v.Replace("%ThemeDir%", sourceDir));
            // xp icon and cursor values often name system dlls/cursor files (shell32.dll,32; Cursors\*.cur) whose
            // index/art means something else on Windows 10, so those slots only take pictures the pack shipped.
            if (!packOnly && File.Exists(expanded))
                return "%WinDir%" + expanded.Substring(Environment.ExpandEnvironmentVariables("%WinDir%").Length).Replace('/', '\\') + (index != null ? "," + index : "");
            string packed = FindInPack(sourceDir, name);
            if (packed == null) { why = $"\"{name}\" isn't shipped with the pack"; return null; }
            Directory.CreateDirectory(Path.Combine(styleDir, subdir));
            string target = Path.Combine(styleDir, subdir, name);
            // the pack can be reached through the output folder itself (three levels up from a candidate under tmp),
            // and File.Copy onto its own source fails with "used by another process"; identical bytes need no copy.
            if (!string.Equals(Path.GetFullPath(packed), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(target) || !SameFile(packed, target)) File.Copy(packed, target, true);
            }
            return $@"%WinDir%\Resources\Themes\{styleFolder}\{subdir}\{name}" + (index != null ? "," + index : "");
        }

        private static bool SameFile(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            var a0 = File.ReadAllBytes(a);
            var b0 = File.ReadAllBytes(b);
            if (a0.Length != b0.Length) return false;
            for (int i = 0; i < a0.Length; ++i) if (a0[i] != b0[i]) return false;
            return true;
        }

        private static string FindInPack(string dir, string name)
        {
            // the pack's media sits beside the style, beside the theme file one level up, or both (GloWorm's
            // Sounds\ next to its .theme while the .msstyles is a folder deeper).
            var roots = new List<string>();
            string d = dir;
            for (int i = 0; i < 3 && !string.IsNullOrEmpty(d) && Directory.Exists(d); ++i, d = Path.GetDirectoryName(d))
                roots.Add(d);
            foreach (var root in roots)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, name, SearchOption.AllDirectories))
                        if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }
    }

    /// <summary>an .theme's sections in file order, keys case-insensitive, duplicates merged.</summary>
    public class ThemeIni
    {
        public class Part
        {
            public string Name { get; set; }
            public List<KeyValuePair<string, string>> Keys { get; } = new List<KeyValuePair<string, string>>();
        }

        private readonly Dictionary<string, Part> m_byName = new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Part> m_order = new List<Part>();

        public IReadOnlyList<KeyValuePair<string, Part>> Sections =>
            m_order.Select(s => new KeyValuePair<string, Part>(s.Name, s)).ToList();

        public static ThemeIni Read(string path)
        {
            var ini = new ThemeIni();
            Part current = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    if (close <= 0) continue;
                    current = ini.Ensure(line.Substring(1, close - 1).Trim());
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || current == null) continue;
                current.Keys.Add(new KeyValuePair<string, string>(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim()));
            }
            return ini;
        }

        private Part Ensure(string name)
        {
            if (!m_byName.TryGetValue(name, out var s))
            {
                s = new Part { Name = name };
                m_byName[name] = s;
                m_order.Add(s);
            }
            return s;
        }

        public string Get(string section, string key)
        {
            if (!m_byName.TryGetValue(section, out var s)) return null;
            string v = null;
            foreach (var kv in s.Keys)
                if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) v = kv.Value;
            return v;
        }
    }
}
