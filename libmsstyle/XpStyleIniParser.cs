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
            var byName = new Dictionary<string, StyleClass>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in ParseIniSections(DecodeUtf16LE(data)))
            {
                ParseSection(section, classes, byName, diagnostics);
            }
        }

        private static void ParseSection(IniSection section, Dictionary<int, StyleClass> classes,
            Dictionary<string, StyleClass> byName, List<string> diag)
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
                var prop = ParseProperty(kv.Key, kv.Value, styleClass.ClassId, partId, stateId, out string why);
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

        internal static StyleProperty ParseProperty(string name, string value, int classId, int partId, int stateId, out string error)
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
            if (value.Contains("##"))
            {
                error = "unresolved ## substitution";
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
                    if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1") prop.SetValue(true);
                    else if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0") prop.SetValue(false);
                    else { error = "not a bool"; return null; }
                    break;
                case IDENTIFIER.INT:
                case IDENTIFIER.SIZE:
                    if (!Ints(v, 1, 1, out n)) { error = "not an integer"; return null; }
                    prop.SetValue(n[0]);
                    break;
                case IDENTIFIER.COLOR:
                    if (!Ints(v, 3, 3, out n) || n.Any(c => c < 0 || c > 255)) { error = "not an \"r g b\" color"; return null; }
                    prop.SetValue(Color.FromArgb(n[0], n[1], n[2]));
                    break;
                case IDENTIFIER.MARGINS:
                    // XP MARGINS is cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight (uxtheme.h).
                    if (!Ints(v, 4, 4, out n)) { error = "not 4 margins"; return null; }
                    prop.SetValue(new Margins(n[0], n[2], n[1], n[3]));
                    break;
                case IDENTIFIER.RECTTYPE:
                    if (!Ints(v, 4, 4, out n)) { error = "not a 4 value rect"; return null; }
                    prop.SetValue(new Margins(n[0], n[1], n[2], n[3]));
                    break;
                case IDENTIFIER.POSITION:
                    if (!Ints(v, 2, 2, out n)) { error = "not an x, y position"; return null; }
                    prop.SetValue(new Size(n[0], n[1]));
                    break;
                case IDENTIFIER.INTLIST:
                    if (!Ints(v, 1, int.MaxValue, out n)) { error = "not an integer list"; return null; }
                    prop.SetValue(n.ToList());
                    break;
                default: // STRING, FILENAME, FONT
                    if (v.Length == 0) { error = "empty value"; return null; }
                    prop.SetValue(v);
                    break;
            }
            return prop;
        }

        private static bool Ints(string v, int min, int max, out int[] result)
        {
            var parts = v.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            result = new int[parts.Length];
            if (parts.Length < min || parts.Length > max) return false;
            for (int i = 0; i < parts.Length; ++i)
            {
                if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result[i])) return false;
            }
            return true;
        }

        #region INI text

        internal class IniSection
        {
            public string Name;
            /// <summary>keys in file order, the last occurrence of a repeated key wins.</summary>
            public List<KeyValuePair<string, string>> Properties = new List<KeyValuePair<string, string>>();
            public string Get(string key) => Properties.LastOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        }

        private static string DecodeUtf16LE(byte[] data)
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
