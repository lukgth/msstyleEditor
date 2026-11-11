using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace libmsstyle
{
    /// <summary>
    /// Represents color scheme info parsed from THEMES_INI.
    /// </summary>
    public class XpColorScheme
    {
        public string InternalName { get; set; } // e.g. "NormalColor", "Metallic", "HomeStead"
        public string DisplayName { get; set; }  // e.g. "Default (blue)", "Silver", "Olive Green"
        public string IniResourceName { get; set; } // e.g. "NORMALBLUE_INI", the NormalSize file
        /// <summary>the other sizes xp offers for this scheme (Font size in Display Properties), NormalSize excluded.</summary>
        public List<XpSize> Sizes { get; } = new List<XpSize>();
        /// <summary>what XP showed for NormalSize under Font size (packed string 3000+index); null = "Normal".</summary>
        public string NormalSizeDisplayName { get; set; }
        public override string ToString() => DisplayName == InternalName ? InternalName : $"{DisplayName} ({InternalName})";
    }

    /// <summary>a [Size.X] of THEMES_INI and the scheme INI drawing that size.</summary>
    public class XpSize
    {
        public string InternalName { get; set; } // "LargeFonts", "ExtraLargeFonts"
        public string DisplayName { get; set; }  // "Large Fonts"
        public string IniResourceName { get; set; } // "LARGEFONTSBLUE_INI"
    }

    /// <summary>
    /// The [ColorScheme.X] hue/color table of THEMES_INI and the substitution themeldr applies to
    /// every color it reads while parsing the scheme INI (parser.cpp ApplyColorSubstitution): an exact
    /// match against FromColorN becomes ToColorN, otherwise a hue match against FromHueN keeps the
    /// color's luminance and saturation and takes ToHueN. This is what makes the silver and olive
    /// schemes out of the blue bitmaps and colors, so a port without it produces three identical styles.
    /// </summary>
    public class XpColorSubstitution
    {
        // utils.cpp RGBtoHLS/HLStoRGB: hue is 0..240, achromatic colors report hue 160.
        private const int HlsMax = 240;
        private const int RgbMax = 255;
        private const int UndefinedHue = HlsMax * 2 / 3;

        private readonly List<Color> m_fromColors = new List<Color>();
        private readonly List<Color> m_toColors = new List<Color>();
        private readonly List<int> m_fromHues = new List<int>();
        private readonly List<int> m_toHues = new List<int>();

        /// <summary>no table, or a table whose every pair maps a value onto itself: nothing to substitute.</summary>
        public bool IsEmpty => m_fromColors.Count == 0 && m_fromHues.Count == 0;

        /// <summary>
        /// Reads [ColorScheme.&lt;schemeName&gt;] out of THEMES_INI. Null when the section or every
        /// pair is missing, so callers can skip the substitution entirely.
        /// </summary>
        public static XpColorSubstitution FromThemesIni(byte[] themesIni, string schemeName)
        {
            if (themesIni == null || string.IsNullOrEmpty(schemeName)) return null;
            var section = XpStyleIniParser.ParseIniSections(XpStyleIniParser.DecodeUtf16LE(themesIni))
                .FirstOrDefault(s => s.Name.Equals("ColorScheme." + schemeName, StringComparison.OrdinalIgnoreCase));
            if (section == null) return null;

            var sub = new XpColorSubstitution();
            var fromColors = new Color[5];
            var toColors = new Color[5];
            var fromHues = new int[5];
            var toHues = new int[5];
            bool hasColors = false, hasHues = false;
            for (int i = 0; i < 5; ++i)
            {
                if (TryParseColor(section.Get("FromColor" + (i + 1)), out fromColors[i])
                    && TryParseColor(section.Get("ToColor" + (i + 1)), out toColors[i]))
                    hasColors = true;
                if (TryParseInt(section.Get("FromHue" + (i + 1)), out fromHues[i])
                    && TryParseInt(section.Get("ToHue" + (i + 1)), out toHues[i]))
                    hasHues = true;
            }

            // parser.cpp keeps a pair only while it actually changes something, trimming the
            // trailing pairs that map a value onto itself.
            if (hasColors)
            {
                int count = 5;
                while (count > 0 && SameRgb(fromColors[count - 1], toColors[count - 1])) --count;
                for (int i = 0; i < count; ++i)
                {
                    sub.m_fromColors.Add(fromColors[i]);
                    sub.m_toColors.Add(toColors[i]);
                }
            }
            if (hasHues)
            {
                int count = 5;
                while (count > 0 && fromHues[count - 1] == toHues[count - 1]) --count;
                for (int i = 0; i < count; ++i)
                {
                    sub.m_fromHues.Add(fromHues[i]);
                    sub.m_toHues.Add(toHues[i]);
                }
            }
            return sub.IsEmpty ? null : sub;
        }

        /// <summary>the substituted color, or <paramref name="color"/> when no entry matches.</summary>
        public Color Apply(Color color)
        {
            for (int i = 0; i < m_fromColors.Count; ++i)
            {
                if (SameRgb(color, m_fromColors[i])) return m_toColors[i];
            }
            if (m_fromHues.Count == 0) return color;

            RgbToHls(color, out int hue, out int lum, out int sat);
            for (int i = 0; i < m_fromHues.Count; ++i)
            {
                if (hue == m_fromHues[i]) return HlsToRgb(m_toHues[i], lum, sat);
            }
            return color;
        }

        private static bool SameRgb(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

        private static bool TryParseColor(string text, out Color color)
        {
            color = Color.Empty;
            if (text == null) return false;
            if (!XpStyleIniParser.GetIntList(text, XpStyleIniParser.ColorParts, 3, 0, 255, out int[] n, out _)) return false;
            color = Color.FromArgb(n[0], n[1], n[2]);
            return true;
        }

        private static bool TryParseInt(string text, out int value)
        {
            value = 0;
            if (text == null) return false;
            if (!XpStyleIniParser.GetIntList(text, null, 1, 0, 0, out int[] n, out _)) return false;
            value = n[0];
            return true;
        }

        /// <summary>utils.cpp RGBtoHLS: hue 0..240 (160 for greys), luminance and saturation 0..240.</summary>
        private static void RgbToHls(Color color, out int hue, out int lum, out int sat)
        {
            int r = color.R, g = color.G, b = color.B;
            int cMax = Math.Max(r, Math.Max(g, b));
            int cMin = Math.Min(r, Math.Min(g, b));
            int cSum = cMax + cMin;
            lum = (cSum * HlsMax + RgbMax) / (2 * RgbMax);

            int cDif = cMax - cMin;
            if (cDif == 0)
            {
                sat = 0;
                hue = UndefinedHue;
                return;
            }
            sat = lum <= HlsMax / 2
                ? (cDif * HlsMax + cSum / 2) / cSum
                : (cDif * HlsMax + (2 * RgbMax - cSum) / 2) / (2 * RgbMax - cSum);

            int rDelta = ((cMax - r) * (HlsMax / 6) + cDif / 2) / cDif;
            int gDelta = ((cMax - g) * (HlsMax / 6) + cDif / 2) / cDif;
            int bDelta = ((cMax - b) * (HlsMax / 6) + cDif / 2) / cDif;
            if (r == cMax) hue = bDelta - gDelta;
            else if (g == cMax) hue = HlsMax / 3 + rDelta - bDelta;
            else hue = 2 * HlsMax / 3 + gDelta - rDelta;
            if (hue < 0) hue += HlsMax;
            if (hue >= HlsMax) hue -= HlsMax;
        }

        /// <summary>utils.cpp HLStoRGB.</summary>
        private static Color HlsToRgb(int hue, int lum, int sat)
        {
            if (sat == 0)
            {
                int v = lum * RgbMax / HlsMax;
                return Color.FromArgb(v, v, v);
            }
            int magic2 = lum <= HlsMax / 2
                ? (lum * (HlsMax + sat) + HlsMax / 2) / HlsMax
                : lum + sat - (lum * sat + HlsMax / 2) / HlsMax;
            int magic1 = 2 * lum - magic2;
            int r = (HueToRgb(magic1, magic2, hue + HlsMax / 3) * RgbMax + HlsMax / 2) / HlsMax;
            int g = (HueToRgb(magic1, magic2, hue) * RgbMax + HlsMax / 2) / HlsMax;
            int b = (HueToRgb(magic1, magic2, hue - HlsMax / 3) * RgbMax + HlsMax / 2) / HlsMax;
            return Color.FromArgb(r, g, b);
        }

        private static int HueToRgb(int n1, int n2, int hue)
        {
            if (hue >= HlsMax) hue -= HlsMax;
            if (hue < HlsMax / 6) return n1 + ((n2 - n1) * hue + HlsMax / 12) / (HlsMax / 6);
            if (hue < HlsMax / 2) return n2;
            if (hue < HlsMax * 2 / 3) return n1 + ((n2 - n1) * (HlsMax * 2 / 3 - hue) + HlsMax / 12) / (HlsMax / 6);
            return n1;
        }
    }

    /// <summary>
    /// Parses THEMES_INI and the per scheme INI resources of a packed Windows XP .msstyles.
    /// Class, part and state ids come from the XP schema (<see cref="XpSchema"/>, generated
    /// from tmschema.h), resolved the way XP's themeldr parser.cpp does it:
    /// [app::class.part(state)], parts from the "&lt;class&gt;PARTS" enum, states from
    /// "&lt;part&gt;STATES" (or "&lt;class&gt;STATES" without a part). Anything XP itself would
    /// reject is skipped and reported in the diagnostics list instead of guessed.
    /// </summary>
    public static class XpStyleIniParser
    {
        /// <summary>
        /// Parses UTF-16LE THEMES_INI resource to extract color scheme information: the NormalSize file of every
        /// scheme, and its other sizes in <see cref="XpColorScheme.Sizes"/>. Schemes without a NormalSize file are dropped.
        /// </summary>
        public static List<XpColorScheme> ParseThemesIni(byte[] data)
        {
            var sections = ParseIniSections(DecodeUtf16LE(data));

            var schemes = new Dictionary<string, XpColorScheme>(StringComparer.OrdinalIgnoreCase);
            var sizeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in sections)
            {
                if (section.Name.StartsWith("Size.", StringComparison.OrdinalIgnoreCase))
                {
                    string sizeName = section.Name.Substring("Size.".Length);
                    sizeNames[sizeName] = section.Get("displayname") ?? sizeName;
                }
                if (!section.Name.StartsWith("ColorScheme.", StringComparison.OrdinalIgnoreCase)) continue;
                string schemeName = section.Name.Substring("ColorScheme.".Length);
                schemes[schemeName] = new XpColorScheme
                {
                    InternalName = schemeName,
                    DisplayName = section.Get("displayname") ?? schemeName,
                };
            }

            foreach (var section in sections)
            {
                if (!section.Name.StartsWith("File.", StringComparison.OrdinalIgnoreCase)) continue;
                string sizes = section.Get("sizes"), colorScheme = section.Get("colorschemes");
                if (sizes == null || colorScheme == null) continue;

                // Some themes use DisplayName in ColorSchemes (e.g. "Lakrits" instead of "NormalColor").
                if (!schemes.TryGetValue(colorScheme, out var matched))
                    matched = schemes.Values.FirstOrDefault(s => s.DisplayName.Equals(colorScheme, StringComparison.OrdinalIgnoreCase));
                if (matched == null) continue;
                string ini = section.Name.Substring("File.".Length).ToUpperInvariant() + "_INI";
                if (sizes.Equals("NormalSize", StringComparison.OrdinalIgnoreCase))
                    matched.IniResourceName = ini;
                else if (!matched.Sizes.Any(s => s.InternalName.Equals(sizes, StringComparison.OrdinalIgnoreCase)))
                    matched.Sizes.Add(new XpSize
                    {
                        InternalName = sizes,
                        DisplayName = sizeNames.TryGetValue(sizes, out var dn) ? dn : sizes,
                        IniResourceName = ini,
                    });
            }

            return schemes.Values.Where(s => !string.IsNullOrEmpty(s.IniResourceName)).ToList();
        }

        /// <summary>
        /// The schemes XP itself loads: packthem's COMBODATA (cColors, cSizes, then sFileNums[size * cColors + color],
        /// -1 = none; themeldr tmutils.cpp COMBOENTRY) indexes FILERESNAMES, COLORNAMES/SIZENAMES give the names.
        /// THEMES_INI only supplies display names. Its [File.X] ColorSchemes lines can name schemes that don't exist
        /// (Lion OS X: "Lion" where the scheme is NormalColor), which packthem resolved when it built the tables.
        /// NormalSize is the scheme's main file (the first size the scheme has when no size is called that).
        /// </summary>
        public static List<XpColorScheme> SchemesFromCombo(byte[] combo, List<string> colorNames, List<string> sizeNames,
            List<string> fileResNames, byte[] themesIni)
        {
            var result = new List<XpColorScheme>();
            if (combo == null || combo.Length < 4) return result;
            int cColors = BitConverter.ToInt16(combo, 0), cSizes = BitConverter.ToInt16(combo, 2);
            if (cColors <= 0 || cSizes <= 0 || combo.Length < 4 + 2 * cColors * cSizes) return result;
            var sections = themesIni == null ? new List<IniSection>() : ParseIniSections(DecodeUtf16LE(themesIni));
            string Display(string prefix, string name)
                => sections.FirstOrDefault(s => s.Name.Equals(prefix + name, StringComparison.OrdinalIgnoreCase))?.Get("displayname") ?? name;

            for (int c = 0; c < cColors && c < colorNames.Count; ++c)
            {
                var files = new List<(string Size, string Ini)>();
                for (int s = 0; s < cSizes && s < sizeNames.Count; ++s)
                {
                    int file = BitConverter.ToInt16(combo, 4 + 2 * (s * cColors + c));
                    if (file >= 0 && file < fileResNames.Count) files.Add((sizeNames[s], fileResNames[file]));
                }
                if (files.Count == 0) continue;
                int main = files.FindIndex(f => f.Size.Equals("NormalSize", StringComparison.OrdinalIgnoreCase));
                if (main < 0) main = 0;
                var scheme = new XpColorScheme
                {
                    InternalName = colorNames[c],
                    DisplayName = Display("ColorScheme.", colorNames[c]),
                    IniResourceName = files[main].Ini,
                };
                for (int i = 0; i < files.Count; ++i)
                    if (i != main)
                        scheme.Sizes.Add(new XpSize { InternalName = files[i].Size, DisplayName = Display("Size.", files[i].Size), IniResourceName = files[i].Ini });
                result.Add(scheme);
            }
            return result;
        }

        /// <summary>[Documentation] DisplayName of THEMES_INI, null when it has none.</summary>
        public static string ThemeDisplayName(byte[] themesIni)
            => ParseIniSections(DecodeUtf16LE(themesIni)).FirstOrDefault(s => s.Name.Equals("Documentation", StringComparison.OrdinalIgnoreCase))?.Get("displayname");

        /// <summary>
        /// Parses a scheme INI resource (e.g. NORMALBLUE_INI) into <paramref name="classes"/>.
        /// IMAGEFILE-like FILENAME and FONT properties keep their INI text as a string value;
        /// <see cref="VisualStyle"/> turns them into resource / string ids afterwards.
        /// </summary>
        public static void ParseSchemeIni(byte[] data, Dictionary<int, StyleClass> classes, List<string> diagnostics)
        {
            ParseSchemeIni(data, classes, diagnostics, null, null);
        }

        /// <param name="substitutions">the scheme's [Subst.X] table from THEMES_INI (see
        /// <see cref="ParseSubstTables"/>); null leaves ##name## values unresolved and reported.</param>
        /// <param name="colorSubstitution">the scheme's [ColorScheme.X] hue/color substitution (see
        /// <see cref="XpColorSubstitution.FromThemesIni"/>); null keeps every color as written.</param>
        public static void ParseSchemeIni(byte[] data, Dictionary<int, StyleClass> classes, List<string> diagnostics,
            IDictionary<string, string> substitutions, XpColorSubstitution colorSubstitution)
        {
            var byName = new Dictionary<string, StyleClass>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in ParseIniSections(DecodeUtf16LE(data)))
            {
                ParseSection(section, classes, byName, diagnostics, substitutions, colorSubstitution);
            }
        }

        /// <summary>
        /// The [Subst.X] tables of THEMES_INI, keyed by table name. "Include = Other" pulls the other
        /// table's symbols in at that point, the way packthem's TCB_SUBSTTABLE handler does it, and the
        /// first definition of a symbol wins (packthem GetSubstValue scans in insertion order).
        /// </summary>
        public static Dictionary<string, Dictionary<string, string>> ParseSubstTables(byte[] themesIni)
        {
            var raw = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in ParseIniSections(DecodeUtf16LE(themesIni)))
            {
                if (section.Name.StartsWith("Subst.", StringComparison.OrdinalIgnoreCase))
                    raw[section.Name.Substring("Subst.".Length)] = section.Properties;
            }

            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            void Expand(string name, List<string> path)
            {
                if (result.ContainsKey(name) || path.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
                if (!raw.TryGetValue(name, out var entries)) return;
                path.Add(name);
                var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in entries)
                {
                    if (kv.Key.Equals("Include", StringComparison.OrdinalIgnoreCase))
                    {
                        Expand(kv.Value.Trim(), path);
                        if (result.TryGetValue(kv.Value.Trim(), out var included))
                            foreach (var sym in included)
                                if (!table.ContainsKey(sym.Key)) table[sym.Key] = sym.Value;
                        continue;
                    }
                    if (!table.ContainsKey(kv.Key)) table[kv.Key] = kv.Value;
                }
                path.RemoveAt(path.Count - 1);
                result[name] = table;
            }
            foreach (var name in raw.Keys.ToList()) Expand(name, new List<string>());
            return result;
        }

        /// <summary>
        /// The [Subst] table packthem substitutes into one scheme INI. parser.cpp ParseFileSection
        /// turns [File.X] into table X and then includes the ColorSchemes and Sizes tables named in
        /// that section, so "Blue\Blue.ini" is substituted with [Subst.NormalBlue] = [Subst.NormalColor]
        /// + [Subst.NormalSize]. Null when the container has no matching [File] section.
        /// </summary>
        public static IDictionary<string, string> SubstTableFor(byte[] themesIni, string iniResourceName)
        {
            if (themesIni == null || string.IsNullOrEmpty(iniResourceName)) return null;
            var sections = ParseIniSections(DecodeUtf16LE(themesIni));
            var file = sections.FirstOrDefault(s => s.Name.StartsWith("File.", StringComparison.OrdinalIgnoreCase)
                && (s.Name.Substring("File.".Length).ToUpperInvariant() + "_INI")
                    .Equals(iniResourceName, StringComparison.OrdinalIgnoreCase));
            if (file == null) return null;

            var tables = ParseSubstTables(themesIni);
            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string name)
            {
                if (name == null || !tables.TryGetValue(name.Trim(), out var entries)) return;
                foreach (var kv in entries)
                    if (!table.ContainsKey(kv.Key)) table[kv.Key] = kv.Value;
            }
            Add(file.Name.Substring("File.".Length));
            Add(file.Get("ColorSchemes"));
            Add(file.Get("Sizes"));
            return table.Count == 0 ? null : table;
        }

        /// <summary>
        /// Replaces ##name## (or ##name, packthem doesn't need the closing pair) with the table's value.
        /// Returns null and the symbol name when the table has no such symbol, which is what XP reports
        /// and refuses the property for.
        /// </summary>
        private static string Substitute(string value, IDictionary<string, string> table, out string missing)
        {
            missing = null;
            if (table == null || value.IndexOf("##", StringComparison.Ordinal) < 0) return value;
            var sb = new StringBuilder(value.Length);
            int i = 0;
            while (i < value.Length)
            {
                int at = value.IndexOf("##", i, StringComparison.Ordinal);
                if (at < 0) { sb.Append(value, i, value.Length - i); break; }
                sb.Append(value, i, at - i);
                int nameStart = at + 2, end = nameStart;
                while (end < value.Length && IsNameChar(value[end])) ++end;
                string name = value.Substring(nameStart, end - nameStart);
                if (name.Length == 0 || !table.TryGetValue(name, out string replacement))
                {
                    missing = name;
                    return null;
                }
                sb.Append(replacement);
                i = end;
            }
            return sb.ToString();
        }

        private static void ParseSection(IniSection section, Dictionary<int, StyleClass> classes,
            Dictionary<string, StyleClass> byName, List<string> diag,
            IDictionary<string, string> substitutions, XpColorSubstitution colorSubstitution)
        {
            if (!TryParseSectionName(section.Name, out string fullClass, out string cls, out string part, out string state, out string err))
            {
                diag.Add($"[{section.Name}]: {err}; section skipped");
                return;
            }

            int partId = 0, stateId = 0;
            if (part != null)
            {
                if (!XpSchema.Enums.TryGetValue(cls + "PARTS", out var parts))
                {
                    diag.Add($"[{section.Name}]: class {cls} has no parts in the XP schema; section skipped");
                    return;
                }
                if (!parts.TryGetValue(part, out partId))
                {
                    diag.Add($"[{section.Name}]: {part} isn't a part of {cls}; section skipped");
                    return;
                }
            }
            if (state != null)
            {
                string owner = part ?? cls;
                if (!XpSchema.Enums.TryGetValue(owner + "STATES", out var states))
                {
                    diag.Add($"[{section.Name}]: {owner} has no states in the XP schema; section skipped");
                    return;
                }
                if (!states.TryGetValue(state, out stateId))
                {
                    diag.Add($"[{section.Name}]: {state} isn't a state of {owner}; section skipped");
                    return;
                }
            }

            if (!byName.TryGetValue(fullClass, out var styleClass))
            {
                int id = classes.Count == 0 ? 0 : classes.Keys.Max() + 1;
                styleClass = new StyleClass { ClassId = id, ClassName = fullClass };
                byName[fullClass] = styleClass;
                classes[id] = styleClass;
            }
            if (!styleClass.Parts.TryGetValue(partId, out var stylePart))
            {
                stylePart = new StylePart { PartId = partId, PartName = part?.ToUpperInvariant() ?? "Common Properties" };
                styleClass.Parts[partId] = stylePart;
            }
            if (!stylePart.States.TryGetValue(stateId, out var styleState))
            {
                styleState = new StyleState { StateId = stateId, StateName = state?.ToUpperInvariant() ?? "Common Properties" };
                stylePart.States[stateId] = styleState;
            }

            foreach (var kv in section.Properties)
            {
                string text = Substitute(kv.Value, substitutions, out string missing);
                if (text == null)
                {
                    diag.Add($"[{section.Name}] {kv.Key} = {kv.Value}: substitution symbol {missing} isn't in the scheme's [Subst] table; property skipped");
                    continue;
                }
                var prop = ParseProperty(kv.Key, text, styleClass.ClassId, partId, stateId, colorSubstitution, out string why);
                if (prop == null)
                {
                    diag.Add($"[{section.Name}] {kv.Key} = {kv.Value}: {why}; property skipped");
                    continue;
                }
                // a key repeated in a reopened section: the last one wins, like XP.
                styleState.Properties.RemoveAll(p => p.Header.nameID == prop.Header.nameID);
                styleState.Properties.Add(prop);
            }
        }

        /// <summary>[app::class.part(state)] with every piece but the class optional.</summary>
        internal static bool TryParseSectionName(string name, out string fullClass, out string cls, out string part, out string state, out string error)
        {
            fullClass = cls = part = state = error = null;
            string body = name.Trim();
            int open = body.IndexOf('(');
            if (open >= 0)
            {
                int close = body.IndexOf(')', open);
                if (close < 0 || body.Substring(close + 1).Trim().Length > 0)
                {
                    error = "malformed state";
                    return false;
                }
                state = body.Substring(open + 1, close - open - 1).Trim();
                body = body.Substring(0, open).Trim();
            }

            string app = null;
            int colons = body.IndexOf("::", StringComparison.Ordinal);
            if (colons >= 0)
            {
                app = body.Substring(0, colons).Trim();
                body = body.Substring(colons + 2).Trim();
            }
            int dot = body.IndexOf('.');
            if (dot >= 0)
            {
                part = body.Substring(dot + 1).Trim();
                body = body.Substring(0, dot).Trim();
            }
            cls = body;
            if (cls.Length == 0 || (app != null && app.Length == 0) || (part != null && part.Length == 0) || (state != null && state.Length == 0))
            {
                error = "empty name component";
                return false;
            }
            if (app != null && cls.Equals("SysMetrics", StringComparison.OrdinalIgnoreCase))
            {
                error = "SysMetrics can't be scoped to an application";
                return false;
            }
            fullClass = app == null ? cls : app + "::" + cls;
            return true;
        }

        private static readonly Dictionary<string, IDENTIFIER> PrimTypes = new Dictionary<string, IDENTIFIER>(StringComparer.OrdinalIgnoreCase)
        {
            ["ENUM"] = IDENTIFIER.ENUM,
            ["STRING"] = IDENTIFIER.STRING,
            ["INT"] = IDENTIFIER.INT,
            ["BOOL"] = IDENTIFIER.BOOLTYPE,
            ["COLOR"] = IDENTIFIER.COLOR,
            ["MARGINS"] = IDENTIFIER.MARGINS,
            ["FILENAME"] = IDENTIFIER.FILENAME,
            ["SIZE"] = IDENTIFIER.SIZE,
            ["POSITION"] = IDENTIFIER.POSITION,
            ["RECT"] = IDENTIFIER.RECTTYPE,
            ["FONT"] = IDENTIFIER.FONT,
            ["INTLIST"] = IDENTIFIER.INTLIST,
        };

        internal static StyleProperty ParseProperty(string name, string value, int classId, int partId, int stateId,
            XpColorSubstitution colorSubstitution, out string error)
        {
            error = null;
            if (!XpSchema.Properties.TryGetValue(name.Trim(), out var info))
            {
                error = "unknown property";
                return null;
            }
            // the schema's first entries are the primitive types (STRING = 201 ... INTLIST = 211). Only
            // FONT and COLOR are also real properties (TMT_FONT/TMT_COLOR, "Font = Tahoma, 8" in [Rebar]).
            if (info.Id < 300 && info.Id != (int)IDENTIFIER.FONT && info.Id != (int)IDENTIFIER.COLOR)
            {
                error = "type name, not a property";
                return null;
            }
            if (!PrimTypes.TryGetValue(info.Prim, out var type))
            {
                error = $"unsupported XP type {info.Prim}";
                return null;
            }
            var header = new PropertyHeader(info.Id, (int)type) { classID = classId, partID = partId, stateID = stateId };
            var prop = new StyleProperty(header);
            string v = value.Trim();
            int[] n;
            switch (type)
            {
                case IDENTIFIER.ENUM:
                    if (!XpSchema.Enums.TryGetValue(name.Trim(), out var values) || !values.TryGetValue(v, out int ev))
                    {
                        error = "unknown enum value";
                        return null;
                    }
                    prop.SetValue(ev);
                    break;
                case IDENTIFIER.BOOLTYPE:
                    // parser.cpp ParseBoolValue only knows true/false; the 1/0 forms are kept for
                    // hand written INIs XP's own parser would reject.
                    if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1") prop.SetValue(true);
                    else if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0") prop.SetValue(false);
                    else { error = "not a bool"; return null; }
                    break;
                case IDENTIFIER.INT:
                    if (!GetIntList(v, null, 1, 0, 0, out n, out error)) return null;
                    prop.SetValue(n[0]);
                    break;
                case IDENTIFIER.SIZE:
                    if (!GetSize(v, out int pixels, out error)) return null;
                    prop.SetValue(pixels);
                    break;
                case IDENTIFIER.COLOR:
                    // parser.cpp ParseColorValue runs every color through ApplyColorSubstitution
                    // unless it is reading the [ColorScheme] section itself.
                    if (!GetIntList(v, ColorParts, 3, 0, 255, out n, out error)) return null;
                    var color = Color.FromArgb(n[0], n[1], n[2]);
                    prop.SetValue(colorSubstitution == null ? color : colorSubstitution.Apply(color));
                    break;
                case IDENTIFIER.MARGINS:
                    // XP MARGINS is cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight (uxtheme.h).
                    if (!GetIntList(v, MarginParts, 4, 0, 0, out n, out error)) return null;
                    prop.SetValue(new Margins(n[0], n[2], n[1], n[3]));
                    break;
                case IDENTIFIER.RECTTYPE:
                    if (!GetIntList(v, RectParts, 4, 0, 0, out n, out error)) return null;
                    prop.SetValue(new Margins(n[0], n[1], n[2], n[3]));
                    break;
                case IDENTIFIER.POSITION:
                    if (!GetIntList(v, PositionParts, 2, 0, 0, out n, out error)) return null;
                    prop.SetValue(new Size(n[0], n[1]));
                    break;
                case IDENTIFIER.INTLIST:
                    if (!GetIntListValue(v, out n, out error)) return null;
                    prop.SetValue(n.ToList());
                    break;
                default: // STRING, FILENAME, FONT
                    if (v.Length == 0) { error = "empty value"; return null; }
                    prop.SetValue(v);
                    break;
            }
            return prop;
        }

        // the named parts parser.cpp hands to GetIntList, in the order the values are stored.
        internal static readonly string[] ColorParts = { "r", "g", "b" };
        private static readonly string[] MarginParts = { "lw", "rw", "th", "bh" };
        private static readonly string[] RectParts = { "l", "t", "r", "b" };
        private static readonly string[] PositionParts = { "x", "y" };

        /// <summary>THEME_DPI: parser.cpp converts every size unit at 96 dpi and scales later.</summary>
        private const int ThemeDpi = 96;

        /// <summary>
        /// parser.cpp GetIntList: either a named list ("l:1, t:2, r:3, b:4", any order, each part
        /// once) or <paramref name="count"/> numbers separated by optional commas. A range check runs
        /// only when min != max, exactly like XP.
        /// </summary>
        internal static bool GetIntList(string text, string[] parts, int count, int min, int max, out int[] result, out string error)
        {
            error = null;
            result = new int[count];
            var set = new bool[count];
            int i = 0;
            if (text.IndexOf(':') >= 0)
            {
                if (parts == null) { error = "named values aren't allowed here"; return false; }
                for (int k = 0; k < count; ++k)
                {
                    string part = ReadId(text, ref i);
                    if (part == null) { error = "value name expected"; return false; }
                    int slot = Array.FindIndex(parts, p => p.Equals(part, StringComparison.OrdinalIgnoreCase));
                    if (slot < 0) { error = $"unknown value name {part}"; return false; }
                    if (set[slot]) { error = $"{part} given twice"; return false; }
                    SkipSpaces(text, ref i);
                    if (i >= text.Length || text[i] != ':') { error = $"expected ':' after {part}"; return false; }
                    ++i;
                    if (!ReadNumber(text, ref i, out result[slot])) { error = "number expected"; return false; }
                    set[slot] = true;
                    SkipSpaces(text, ref i);
                    if (i < text.Length && text[i] == ',') ++i;
                }
            }
            else
            {
                for (int k = 0; k < count; ++k)
                {
                    if (!ReadNumber(text, ref i, out result[k])) { error = "number expected"; return false; }
                    SkipSpaces(text, ref i);
                    if (i < text.Length && text[i] == ',') ++i;
                }
            }
            if (min != max)
            {
                foreach (int value in result)
                {
                    if (value < min || value > max) { error = $"{value} is out of range"; return false; }
                }
            }
            return true;
        }

        /// <summary>parser.cpp ParseIntListValue: numbers until the end of the line, optional commas.</summary>
        private static bool GetIntListValue(string text, out int[] result, out string error)
        {
            error = null;
            result = null;
            var values = new List<int>();
            int i = 0;
            while (true)
            {
                if (!ReadNumber(text, ref i, out int value))
                {
                    if (values.Count == 0) { error = "number expected"; return false; }
                    break;
                }
                values.Add(value);
                SkipSpaces(text, ref i);
                if (i < text.Length && text[i] == ',') ++i;
                else break;
            }
            result = values.ToArray();
            return true;
        }

        /// <summary>
        /// parser.cpp ParseSizeValue + ParseSizeInfoUnits: a number, optionally followed by one of
        /// pixels/twips/points, converted to pixels at THEME_DPI. Anything else is a parse error there
        /// too (PARSER_IDS_UNKNOWN_SIZE_UNITS).
        /// </summary>
        private static bool GetSize(string text, out int pixels, out string error)
        {
            error = null;
            pixels = 0;
            int i = 0;
            if (!ReadNumber(text, ref i, out int value)) { error = "number expected"; return false; }
            string units = ReadId(text, ref i);
            if (units == null)
            {
                pixels = value;
                return true;
            }
            if (units.Equals("pixels", StringComparison.OrdinalIgnoreCase)) pixels = value;
            else if (units.Equals("twips", StringComparison.OrdinalIgnoreCase)) pixels = -MulDiv(value, ThemeDpi, 20 * 72);
            else if (units.Equals("points", StringComparison.OrdinalIgnoreCase)) pixels = -MulDiv(value, ThemeDpi, 72);
            else { error = $"unknown size units {units}"; return false; }
            return true;
        }

        /// <summary>MulDiv from the win32 api: rounds to nearest, saturates instead of overflowing.</summary>
        private static int MulDiv(int number, int numerator, int denominator)
        {
            long v = (long)number * numerator;
            v = v >= 0 ? (v + denominator / 2) / denominator : -((-v + denominator / 2) / denominator);
            if (v > int.MaxValue) return int.MaxValue;
            if (v < int.MinValue) return int.MinValue;
            return (int)v;
        }

        private static void SkipSpaces(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) ++i;
        }

        /// <summary>
        /// scanner.cpp GetNumber: optional sign, then decimal or 0x/0X hex digits. Returns false when
        /// there is no digit to read, which is what the parser reports as "number expected".
        /// </summary>
        private static bool ReadNumber(string s, ref int i, out int value)
        {
            value = 0;
            SkipSpaces(s, ref i);
            int sign = 1;
            if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            {
                if (s[i] == '-') sign = -1;
                ++i;
            }
            bool hex = i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X');
            if (hex) i += 2;
            long acc = 0;
            int digits = 0;
            for (; i < s.Length; ++i)
            {
                int d = hex
                    ? Uri.IsHexDigit(s[i]) ? Uri.FromHex(s[i]) : -1
                    : s[i] >= '0' && s[i] <= '9' ? s[i] - '0' : -1;
                if (d < 0) break;
                acc = unchecked(acc * (hex ? 16 : 10) + d);
                ++digits;
            }
            if (digits == 0) return false;
            value = unchecked((int)(acc * sign));
            return true;
        }

        /// <summary>scanner.cpp GetId: letters, digits, '_' and '-'.</summary>
        private static string ReadId(string s, ref int i)
        {
            SkipSpaces(s, ref i);
            int start = i;
            while (i < s.Length && IsNameChar(s[i])) ++i;
            return i == start ? null : s.Substring(start, i - start);
        }

        private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';

        #region INI text

        internal class IniSection
        {
            public string Name;
            /// <summary>keys in file order, the last occurrence of a repeated key wins.</summary>
            public List<KeyValuePair<string, string>> Properties = new List<KeyValuePair<string, string>>();
            public string Get(string key) => Properties.LastOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        }

        internal static string DecodeUtf16LE(byte[] data)
        {
            if (data == null || data.Length < 2) return string.Empty;
            int start = data[0] == 0xFF && data[1] == 0xFE ? 2 : 0;
            return Encoding.Unicode.GetString(data, start, data.Length - start).TrimEnd('\0');
        }

        /// <summary>
        /// Sections in file order; a reopened section continues the first one. ';' starts a
        /// comment anywhere on a line, the way the XP scanner reads it.
        /// </summary>
        internal static List<IniSection> ParseIniSections(string text)
        {
            var list = new List<IniSection>();
            var byName = new Dictionary<string, IniSection>(StringComparer.OrdinalIgnoreCase);
            IniSection current = null;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int semi = line.IndexOf(';');
                    if (semi >= 0) line = line.Substring(0, semi);
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("["))
                    {
                        int end = line.IndexOf(']');
                        if (end < 0) { current = null; continue; }
                        string name = line.Substring(1, end - 1).Trim();
                        if (!byName.TryGetValue(name, out current))
                        {
                            current = new IniSection { Name = name };
                            byName[name] = current;
                            list.Add(current);
                        }
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (current != null && eq > 0)
                    {
                        string key = line.Substring(0, eq).Trim();
                        current.Properties.RemoveAll(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                        current.Properties.Add(new KeyValuePair<string, string>(key, line.Substring(eq + 1).Trim()));
                    }
                }
            }
            return list;
        }

        #endregion

        /// <summary>
        /// Converts an INI image file reference (e.g. "Blue\CloseButton.bmp") to a PE BITMAP resource name.
        /// Convention from luna.rc: path separators → '_', '.' → '_', uppercase.
        /// E.g. "Blue\CloseButton.bmp" → "BLUE_CLOSEBUTTON_BMP"
        /// </summary>
        public static string ImageFileToResourceName(string imageFile)
        {
            if (string.IsNullOrEmpty(imageFile)) return string.Empty;
            return imageFile.Trim().Trim('"').Replace('\\', '_').Replace('/', '_').Replace('.', '_').ToUpperInvariant();
        }
    }
}
