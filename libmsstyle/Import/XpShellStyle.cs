using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace libmsstyle.Import
{
    /// <summary>
    /// A Windows XP shellstyle.dll: the task pane stylesheet (UIFILE 1, XP's css-like DUI syntax:
    /// &lt;style resid=X&gt; selector { prop: value; }) and the RT_BITMAP resources it references
    /// through rcbmp(id, mode, color, ...).
    /// </summary>
    public class XpShellStyle : IDisposable
    {
        private IntPtr m_module;
        public string Path { get; }

        // resid -> normalized selector -> property -> value (last one wins, like the DUI parser).
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> m_rules =
            new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

        /// <param name="sysColors">
        /// the theme's system colors by lowercase DUI name (window, activecaption...). xp's classic shellstyle
        /// writes its colors as those names; with this map <see cref="Get"/> returns them as rgb(...), so the task
        /// pane comes out in the theme's colors like on xp. Null: names stay as written.
        /// </param>
        public XpShellStyle(string path, IReadOnlyDictionary<string, Color> sysColors = null)
        {
            Path = path;
            m_sysColors = sysColors;
            m_module = Win32Api.LoadLibraryEx(path, IntPtr.Zero, Win32Api.LoadLibraryFlags.LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE);
            if (m_module == IntPtr.Zero) throw new IOException($"can't open \"{path}\" as a PE resource file");
            var ui = ResourceAccess.GetResource(m_module, "UIFILE", 1);
            if (ui == null) throw new InvalidDataException($"\"{path}\" has no UIFILE 1; not an xp shellstyle");
            if (ResourceAccess.GetResource(m_module, "SHELL32_UIFILE", 3) != null)
                throw new InvalidDataException($"\"{path}\" is a vista+ shellstyle (SHELL32_UIFILE), not an xp one");
            Parse(Decode(ui));
            var ui2 = ResourceAccess.GetResource(m_module, "UIFILE", 2);
            if (ui2 != null) Parse(Decode(ui2));
        }

        private readonly IReadOnlyDictionary<string, Color> m_sysColors;

        private static string Decode(byte[] b)
        {
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[1] == 0) return Encoding.Unicode.GetString(b);
            return Encoding.GetEncoding(1252).GetString(b);
        }

        internal static string NormalizeSelector(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ").Replace(" [", "[").Replace("] [", "][");

        private void Parse(string text)
        {
            foreach (Match style in Regex.Matches(text, @"<style\s+resid\s*=\s*""?([^\s>""]+)""?\s*>(.*?)</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                if (!m_rules.TryGetValue(style.Groups[1].Value, out var rules))
                    m_rules[style.Groups[1].Value] = rules = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                foreach (Match rule in Regex.Matches(style.Groups[2].Value, @"([^{}]+)\{([^}]*)\}"))
                {
                    string sel = NormalizeSelector(rule.Groups[1].Value);
                    if (!rules.TryGetValue(sel, out var props)) rules[sel] = props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var decl in rule.Groups[2].Value.Split(';'))
                    {
                        int colon = decl.IndexOf(':');
                        if (colon <= 0) continue;
                        props[decl.Substring(0, colon).Trim()] = decl.Substring(colon + 1).Trim();
                    }
                }
            }
        }

        /// <summary>raw value text, or null when the stylesheet/selector/property isn't there.</summary>
        public string Get(string resid, string selector, string property)
        {
            if (!(m_rules.TryGetValue(resid, out var rules) && rules.TryGetValue(NormalizeSelector(selector), out var props)
                && props.TryGetValue(property, out var v))) return null;
            if (m_sysColors != null && m_sysColors.TryGetValue(v.Trim().ToLowerInvariant(), out var c)) return $"rgb({c.R},{c.G},{c.B})";
            return v;
        }

        public byte[] GetBitmap(int id) => ResourceAccess.GetResource(m_module, "#2", (uint)id);

        public IEnumerable<int> BitmapIds()
        {
            var ids = new List<int>();
            Win32Api.EnumResourceNamesEx(m_module, 2, (h, type, name, l) =>
            {
                long v = name.ToInt64();
                if (v > 0 && v <= 0xFFFF) ids.Add((int)v);
                return true;
            }, IntPtr.Zero, Win32Api.EnumResourceFlags.RESOURCE_ENUM_LN, 0);
            return ids;
        }

        public void Dispose()
        {
            if (m_module != IntPtr.Zero) Win32Api.FreeLibrary(m_module);
            m_module = IntPtr.Zero;
        }

        // ---- value parsing (XP DUI syntax) ----------------------------------------------------

        /// <summary>a fill: one color, or the two stops of gradient(c1, c2, mode). Null when unparsable.</summary>
        public static Color[] ParseFill(string v)
        {
            if (v == null) return null;
            var g = Regex.Match(v, @"^gradient\s*\((.*)\)$", RegexOptions.IgnoreCase);
            if (g.Success)
            {
                var args = SplitArgs(g.Groups[1].Value);
                if (args.Count < 2) return null;
                // xp's task pane gradients are written argb(0, r, g, b) and still drawn opaque: stops ignore alpha.
                var c1 = ParseColor(args[0]); var c2 = ParseColor(args[1]);
                if (c1 == null || c2 == null) return null;
                return new[] { Color.FromArgb(255, c1.Value), Color.FromArgb(255, c2.Value) };
            }
            var c = ParseColor(v);
            return c == null ? null : new[] { c.Value };
        }

        /// <summary>rgb(r,g,b), argb(a,r,g,b), #RRGGBB or a color name. System color names resolve in <see cref="Get"/>.</summary>
        public static Color? ParseColor(string v)
        {
            if (v == null) return null;
            v = v.Trim();
            var m = Regex.Match(v, @"^(a?rgb)\s*\(([^)]*)\)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var n = SplitArgs(m.Groups[2].Value).Select(x => int.TryParse(x, out int i) ? i : -1).ToList();
                if (n.Any(i => i < 0 || i > 255)) return null;
                if (m.Groups[1].Value.Length == 4 && n.Count == 4) return Color.FromArgb(n[0], n[1], n[2], n[3]);
                if (m.Groups[1].Value.Length == 3 && n.Count == 3) return Color.FromArgb(n[0], n[1], n[2]);
                return null;
            }
            if (Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$"))
                return Color.FromArgb(int.Parse(v.Substring(1, 2), NumberStyles.HexNumber), int.Parse(v.Substring(3, 2), NumberStyles.HexNumber), int.Parse(v.Substring(5, 2), NumberStyles.HexNumber));
            var named = Color.FromName(v);
            return named.IsKnownColor && !named.IsSystemColor ? named : (Color?)null;
        }

        /// <summary>rect(l, t, r, b) with optional rp/px suffixes.</summary>
        public static Margins ParseRect(string v)
        {
            if (v == null) return null;
            var m = Regex.Match(v, @"^rect\s*\(([^)]*)\)$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var n = SplitArgs(m.Groups[1].Value).Select(x => Regex.Match(x, @"^-?\d+")).ToList();
            if (n.Count != 4 || n.Any(x => !x.Success)) return null;
            return new Margins(int.Parse(n[0].Value), int.Parse(n[1].Value), int.Parse(n[2].Value), int.Parse(n[3].Value));
        }

        /// <summary>
        /// rcbmp(id[, mode, color, ...]): the bitmap id and its color key. mode 6: the third argument is the key;
        /// mode 2: per-pixel alpha, no key. rcbmp(id) alone is DUI's GRAPHIC_TransColor with an automatic key, the
        /// bitmap's top-left pixel (directui parse.y, value.cpp CreateGraphic): <paramref name="autoKey"/> is set.
        /// </summary>
        public static bool ParseRcBmp(string v, out int id, out Color? key) => ParseRcBmp(v, out id, out key, out _);

        public static bool ParseRcBmp(string v, out int id, out Color? key, out bool autoKey)
        {
            id = 0; key = null; autoKey = false;
            if (v == null) return false;
            var m = Regex.Match(v, @"^rcbmp\s*\(([^)]*)\)$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            var a = SplitArgs(m.Groups[1].Value);
            if (a.Count < 1 || !int.TryParse(a[0], out id)) return false;
            if (a.Count == 1) { autoKey = true; return true; }
            if (!int.TryParse(a[1], out int mode)) return false;
            if (mode == 6 && a.Count >= 3) key = ParseColor(a[2]);
            return true;
        }

        private static List<string> SplitArgs(string s)
        {
            var r = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; ++i)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                else if (s[i] == ',' && depth == 0) { r.Add(s.Substring(start, i - start).Trim()); start = i + 1; }
            }
            r.Add(s.Substring(start).Trim());
            return r;
        }
    }
}
