using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private static readonly string[] MetricsKeys = { "IconMetrics", "NonclientMetrics" };

        /// <summary>
        /// Reads the extra sections of the .theme the desktop section came from. <paramref name="styleFolder"/> is the
        /// folder name the style is installed under; copied files land in &lt;styleDir&gt; and the emitted paths point
        /// at %WinDir%\Resources\Themes\&lt;styleFolder&gt;. <paramref name="copyFiles"/> false resolves the same files
        /// without copying them (dry runs report what a write would include).
        /// </summary>
        public static XpThemeExtras Read(XpThemeFiles.Desktop desktop, string sourceDir, string styleDir, string styleFolder, bool copyFiles = true)
        {
            var ex = new XpThemeExtras();
            if (desktop?.ThemePath == null) return ex;
            var ini = ThemeIni.Read(desktop.ThemePath);
            // the pack's files are indexed once: resolving every icon, cursor and sound used to walk the source
            // tree again for each of them.
            var pack = new PackIndex(sourceDir);
            // xp's %ThemeDir% is the folder of the .theme being loaded, not the .msstyles folder.
            string themeDir = Path.GetDirectoryName(Path.GetFullPath(desktop.ThemePath));

            foreach (var key in MetricsKeys)
            {
                string v = ini.Get("Metrics", key);
                if (string.IsNullOrWhiteSpace(v)) continue;
                ex.Metrics.Add(new KeyValuePair<string, string>(key, v.Trim()));
            }
            if (ex.Metrics.Count > 0)
                ex.Notes.Add($"metrics: {(ex.Metrics.Count == 2 ? "icon and nonclient" : ex.Metrics[0].Key)} metrics taken from {Path.GetFileName(desktop.ThemePath)}");

            // Pack assets win. System icon indices belong to XP, never the host's shell32/imageres.
            var xpAssets = new XpIconAssets(sourceDir);
            foreach (var kvp in ini.Sections)
            {
                var section = kvp.Value;
                if (!section.Name.EndsWith(@"}\DefaultIcon", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var kv in section.Keys)
                {
                    string resolved = ResolveIcon(kv.Value, pack, xpAssets, themeDir, styleDir, styleFolder, copyFiles, out string why);
                    if (resolved == null)
                    {
                        ex.Warnings.Add($"icons: {section.Name} {kv.Key}: {why}");
                        continue;
                    }
                    ex.Icons.Add((section.Name, kv.Key, resolved));
                    // Win10's desktop uses different CLSIDs for these two XP objects.
                    string modern = section.Name;
                    if (section.Name.Equals(@"CLSID\{450D8FBA-AD25-11D0-98A8-0800361B1103}\DefaultIcon", StringComparison.OrdinalIgnoreCase))
                        modern = @"CLSID\{59031A47-3F72-44A7-89C5-5595FE6B30EE}\DefaultIcon";
                    else if (section.Name.Equals(@"CLSID\{208D2C60-3AEA-1069-A2D7-08002B30309D}\DefaultIcon", StringComparison.OrdinalIgnoreCase))
                        modern = @"CLSID\{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}\DefaultIcon";
                    if (!modern.Equals(section.Name, StringComparison.OrdinalIgnoreCase))
                        ex.Icons.Add((modern, kv.Key, resolved));
                }
            }
            if (ex.Icons.Count > 0) ex.Notes.Add($"icons: {ex.Icons.Count} DefaultIcon entries use pack artwork or grouped icons extracted from XP system files");

            // [Control Panel\Cursors]
            string scheme = ini.Get(@"Control Panel\Cursors", "DefaultValue");
            var cursorValues = new List<KeyValuePair<string, string>>();
            foreach (var key in XpThemeFiles.CursorKeys)
            {
                string v = ini.Get(@"Control Panel\Cursors", key);
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (v.Trim().Equals("Windows default", StringComparison.OrdinalIgnoreCase) || v.Trim().StartsWith("@themeui.dll", StringComparison.OrdinalIgnoreCase))
                {
                    cursorValues.Add(new KeyValuePair<string, string>(key, ""));
                    continue;
                }
                string resolved = Resolve(v, pack, themeDir, styleDir, styleFolder, "Cursors", out _, packOnly: true, copyFiles: copyFiles);
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
                string resolved = Resolve(screensaver, pack, themeDir, styleDir, styleFolder, "Screensavers", out string why, copyFiles: copyFiles);
                if (resolved != null)
                {
                    ex.Screensaver = resolved;
                    ex.Notes.Add($"screensaver: {Path.GetFileName(resolved)}");
                }
                else
                    ex.Warnings.Add($"screensaver: {why}, so [boot] SCRNSAVE.EXE is left empty");
            }

            // [AppEvents\...] sound scheme: only the events whose wav the pack shipped or this machine has.
            foreach (var section in ini.Sections.Where(s => s.Key.StartsWith(@"appevents\schemes\apps\", StringComparison.OrdinalIgnoreCase)).Select(s => s.Value))
            {
                // xp's theme files spell the event's default value "DefaultValue"; the registry (and some packs) call
                // it "@". Either may be present, so take whichever carries a value.
                string v = ini.Get(section.Name, "DefaultValue");
                if (string.IsNullOrWhiteSpace(v)) v = ini.Get(section.Name, "@");
                if (string.IsNullOrWhiteSpace(v)) continue;
                string resolved = Resolve(v, pack, themeDir, styleDir, styleFolder, "Sounds", out string why, copyFiles: copyFiles);
                if (resolved == null)
                {
                    ex.Warnings.Add($"sounds: {why}, so \"{section.Name}\" keeps this machine's wav");
                    continue;
                }
                ex.Sounds.Add((section.Name, resolved));
            }
            if (ex.Sounds.Count > 0) ex.Notes.Add($"sounds: {ex.Sounds.Count} events use wavs the pack shipped");

            return ex;
        }

        private static string ResolveIcon(string raw, PackIndex pack, XpIconAssets assets, string themeDir,
            string styleDir, string styleFolder, bool copyFiles, out string why)
        {
            string value = (raw ?? "").Trim();
            int comma = value.LastIndexOf(','), index = 0;
            bool indexed = comma > 0 && int.TryParse(value.Substring(comma + 1).Trim(), out index);
            string path = (indexed ? value.Substring(0, comma) : value).Trim().Trim('"');
            string name = Path.GetFileName(path.Replace('/', '\\'));
            string packed = pack.Find(name);
            if (packed != null && Path.GetExtension(packed).Equals(".ico", StringComparison.OrdinalIgnoreCase))
                return Resolve(raw, pack, themeDir, styleDir, styleFolder, "Icons", out why, packOnly: true, copyFiles: copyFiles);
            string binary = packed ?? assets.Find(name);
            if (binary == null)
            {
                why = $"XP icon source {name} is absent. Supply original XP {name} under XP_THEME_SYSTEM_ASSETS (XP Windows or flat binary directory), or an ancestor's xp-system-assets directory; no Win10 icon substitution";
                return null;
            }
            try
            {
                byte[] ico = assets.Extract(binary, index);
                string file = XpThemeFiles.SafeName(Path.GetFileNameWithoutExtension(name)) + (index < 0 ? "-id" + (-(long)index) : "-index" + index) + ".ico";
                if (copyFiles)
                {
                    Directory.CreateDirectory(Path.Combine(styleDir, "Icons"));
                    File.WriteAllBytes(Path.Combine(styleDir, "Icons", file), ico);
                }
                why = null;
                return $@"%WinDir%\Resources\Themes\{styleFolder}\Icons\{file},0";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidOperationException)
            {
                why = $"cannot extract XP grouped icon {name},{index}: {e.Message}";
                return null;
            }
        }

        /// <summary>Explicit XP assets, never the host OS. Keeps all RT_GROUP_ICON sizes/depths.</summary>
        private sealed class XpIconAssets
        {
            private readonly List<string> m_roots = new List<string>();
            private readonly Dictionary<string, byte[]> m_icons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            public XpIconAssets(string sourceDir)
            {
                string configured = Environment.GetEnvironmentVariable("XP_THEME_SYSTEM_ASSETS");
                if (!string.IsNullOrWhiteSpace(configured)) m_roots.Add(Path.GetFullPath(configured));
                for (string dir = Path.GetFullPath(sourceDir); dir != null; dir = Path.GetDirectoryName(dir))
                    m_roots.Add(Path.Combine(dir, "xp-system-assets"));
            }

            public string Find(string name)
            {
                foreach (string root in m_roots)
                    foreach (string dir in new[] { root, Path.Combine(root, "System32"), Path.Combine(root, "Windows"), Path.Combine(root, "Windows", "System32") })
                    {
                        string candidate = Path.Combine(dir, name);
                        if (!File.Exists(candidate)) continue;
                        var version = FileVersionInfo.GetVersionInfo(candidate);
                        // XP kernel versions are 5.1/5.2, but its IE6 shell binaries use 6.0.
                        // XP x64 SP2 originals report 6.00.3790.3959; Vista's 6.0.6000+ must not pass.
                        bool xpKernel = version.FileMajorPart == 5 && (version.FileMinorPart == 1 || version.FileMinorPart == 2);
                        bool xpShell = version.FileMajorPart == 6 && version.FileMinorPart == 0
                            && (version.FileBuildPart == 2600 || version.FileBuildPart == 2900 || version.FileBuildPart == 3790);
                        if (xpKernel || xpShell) return candidate;
                    }
                return null;
            }

            public byte[] Extract(string path, int index)
            {
                string key = path + "," + index;
                if (m_icons.TryGetValue(key, out var cached)) return cached;
                using (var resources = new ResourceInventory(path))
                {
                    var groups = resources.OfType("#14").GroupBy(e => e.Name).Select(g => g.First())
                        .OrderBy(e => e.TryGetIntName(out int id) ? id : int.MaxValue).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
                    var group = index < 0 ? groups.FirstOrDefault(e => e.Name == "#" + (-(long)index))
                        : index < groups.Count ? groups[index] : null;
                    if (group == null) throw new InvalidDataException("RT_GROUP_ICON index/resource does not exist");
                    byte[] directory = resources.GetData(group);
                    if (directory == null || directory.Length < 6 || BitConverter.ToUInt16(directory, 0) != 0 || BitConverter.ToUInt16(directory, 2) != 1)
                        throw new InvalidDataException("invalid group icon header");
                    int count = BitConverter.ToUInt16(directory, 4);
                    if (count == 0 || directory.Length < 6 + count * 14) throw new InvalidDataException("truncated group icon directory");
                    var images = new byte[count][];
                    for (int i = 0; i < count; ++i)
                    {
                        string id = "#" + BitConverter.ToUInt16(directory, 6 + i * 14 + 12);
                        var image = resources.OfType("#3").FirstOrDefault(e => e.Name == id && e.Language == group.Language)
                            ?? resources.OfType("#3").FirstOrDefault(e => e.Name == id);
                        images[i] = image == null ? null : resources.GetData(image);
                        if (images[i] == null || images[i].Length != BitConverter.ToUInt32(directory, 6 + i * 14 + 8))
                            throw new InvalidDataException("missing or truncated RT_ICON " + id);
                    }
                    using (var stream = new MemoryStream())
                    using (var writer = new BinaryWriter(stream))
                    {
                        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)count);
                        int offset = 6 + count * 16;
                        for (int i = 0; i < count; ++i)
                        {
                            writer.Write(directory, 6 + i * 14, 8);
                            writer.Write(images[i].Length); writer.Write(offset);
                            offset = checked(offset + images[i].Length);
                        }
                        foreach (byte[] image in images) writer.Write(image);
                        cached = stream.ToArray();
                    }
                }
                m_icons.Add(key, cached);
                return cached;
            }
        }

        /// <summary>
        /// Turns a packed xp path into one Windows 10 can load. System files that exist are kept as they are (absolute
        /// when they live outside %WinDir%); otherwise the file name is looked up inside the pack and copied into
        /// &lt;styleDir&gt;\&lt;subdir&gt;. Icon values keep their ",index" tail. <paramref name="themeDir"/> is the folder of
        /// the .theme, which is what xp's %ThemeDir% names.
        /// </summary>
        private static string Resolve(string raw, PackIndex pack, string themeDir, string styleDir, string styleFolder,
            string subdir, out string why, bool packOnly = false, bool copyFiles = true)
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
            string expanded = ExpandTokens(v, themeDir);
            // xp icon and cursor values often name system dlls/cursor files (shell32.dll,32; Cursors\*.cur) whose
            // index/art means something else on Windows 10, so those slots only take pictures the pack shipped.
            if (!packOnly && File.Exists(expanded))
                return UnderWinDir(expanded) + (index != null ? "," + index : "");
            string packed = pack.Find(name);
            if (packed == null) { why = $"\"{name}\" isn't shipped with the pack"; return null; }
            string target = Path.Combine(styleDir, subdir, name);
            // the pack can be reached through the output folder itself (three levels up from a candidate under tmp),
            // and File.Copy onto its own source fails with "used by another process"; identical bytes need no copy.
            if (copyFiles && !string.Equals(Path.GetFullPath(packed), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.Combine(styleDir, subdir));
                if (!File.Exists(target) || !SameFile(packed, target)) File.Copy(packed, target, true);
            }
            return $@"%WinDir%\Resources\Themes\{styleFolder}\{subdir}\{name}" + (index != null ? "," + index : "");
        }

        /// <summary>xp themefile.cpp ReplaceStringToken: known tokens are replaced and a separator is inserted when
        /// the token is followed by anything but one ("%WinDir%SYSTEM32\shell32.dll"); other %names% are left to the
        /// environment. No path is ever rewritten to a %WinDir% it does not live under.</summary>
        internal static string ExpandTokens(string v, string themeDir)
        {
            if (string.IsNullOrEmpty(v)) return v;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var tokens = new (string Token, string Value)[]
            {
                ("%WinDir%", win), ("%SystemRoot%", win), ("%ThemeDir%", themeDir ?? ""),
                ("%ResourceDir%", Path.Combine(win, "Resources")),
            };
            var sb = new StringBuilder(v.Length + 32);
            for (int i = 0; i < v.Length;)
            {
                bool replaced = false;
                foreach (var t in tokens)
                {
                    if (t.Value.Length == 0 || !string.Equals(v.Substring(i, Math.Min(t.Token.Length, v.Length - i)), t.Token, StringComparison.OrdinalIgnoreCase))
                        continue;
                    sb.Append(t.Value);
                    i += t.Token.Length;
                    // XP inserts the separator only when the replacement doesn't end with one and the path continues.
                    char next = i < v.Length ? v[i] : '\0';
                    if (next != '\0' && next != '\\' && next != '/' && t.Value[t.Value.Length - 1] != '\\' && t.Value[t.Value.Length - 1] != '/')
                        sb.Append('\\');
                    replaced = true;
                    break;
                }
                if (!replaced) sb.Append(v[i++]);
            }
            // %ProgramFiles%, %LOCALAPPDATA% and friends: the environment knows those.
            return Environment.ExpandEnvironmentVariables(sb.ToString());
        }

        /// <summary>the path as a %WinDir%-relative value when it really is under the Windows folder; otherwise the
        /// absolute path unchanged (a rewriter turned C:\Program Files\x.ico into %WinDir%\Program Files\x.ico).</summary>
        private static string UnderWinDir(string expanded)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string full;
            try { full = Path.GetFullPath(expanded).Replace('/', '\\'); }
            catch (Exception) { return expanded; }
            if (full.Length > win.Length && full.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase))
                return "%WinDir%" + full.Substring(win.Length);
            return full;
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

        /// <summary>the pack's files by name, walked once per <see cref="Read"/> instead of once per resolved file.</summary>
        private sealed class PackIndex
        {
            private readonly Dictionary<string, string> m_byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <param name="dir">the folder of the style; the pack's media sits beside it, beside the theme file one
            /// level up, or both (GloWorm's Sounds\ next to its .theme while the .msstyles is a folder deeper). The
            /// nearest root wins.</param>
            public PackIndex(string dir)
            {
                string d = dir;
                for (int i = 0; i < 3 && !string.IsNullOrEmpty(d) && Directory.Exists(d); ++i, d = Path.GetDirectoryName(d))
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                        {
                            string name = Path.GetFileName(f);
                            if (!m_byName.ContainsKey(name)) m_byName[name] = f;
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            public string Find(string name)
                => name != null && m_byName.TryGetValue(name, out string path) ? path : null;
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
            foreach (var raw in XpThemeFiles.ReadThemeLines(path))
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
