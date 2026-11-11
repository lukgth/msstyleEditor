using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;

namespace libmsstyle.Import
{
    /// <summary>
    /// One property of a style, addressed by names instead of the numeric class/part/state ids,
    /// because those ids are not stable across Windows versions (class ids are CMAP order, and
    /// DWMWindow renumbered its parts between 7 and 8.1).
    /// </summary>
    public class SemanticEntry
    {
        public string ClassName { get; set; }
        public int ClassId { get; set; }
        public string PartName { get; set; }
        public int PartId { get; set; }
        public bool PartNameKnown { get; set; }
        public string StateName { get; set; }
        public int StateId { get; set; }
        public int NameId { get; set; }
        public string PropertyName { get; set; }
        public int TypeId { get; set; }
        // occurrence index for the rare case of the same property twice in one state.
        public int Occurrence { get; set; }

        /// <summary>value with resource ids replaced by content hashes, so equal pictures compare equal.</summary>
        public string Value { get; set; }
        /// <summary>value exactly as stored (resource ids included).</summary>
        public string RawValue { get; set; }

        public int ResourceId { get; set; } = -1;
        public string ResourceType { get; set; }
        /// <summary>FONT or a non-image FILENAME: the value is a string-table id (for this style's platform).</summary>
        public bool IsStringRef { get; set; }
        public StyleProperty Property { get; set; }

        public string PartKey => ClassName + "|" + PartName;
        public string StateKey => PartKey + "|" + StateName;
        public string Key => StateKey + "|" + PropertyName + "#" + TypeId + (Occurrence > 0 ? "@" + Occurrence : "");
    }

    public class SemanticModel
    {
        public VisualStyle Style { get; }
        public Platform Platform { get; }
        public List<SemanticEntry> Entries { get; } = new List<SemanticEntry>();
        public Dictionary<string, SemanticEntry> ByKey { get; } = new Dictionary<string, SemanticEntry>(StringComparer.Ordinal);
        public Dictionary<string, StyleClass> ClassesByName { get; } = new Dictionary<string, StyleClass>(StringComparer.OrdinalIgnoreCase);
        /// <summary>class names that appear more than once in the CMAP. Lookups by name are ambiguous for these.</summary>
        public HashSet<string> DuplicateClassNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// The style's [globals] Charset (TMT_CHARSET, 403). XP has no per-font charset: themeldr
        /// parser.cpp keeps this one value and stamps it into every LOGFONT it builds, so every font
        /// imported out of this style carries it. DEFAULT_CHARSET when the style has no such record.
        /// </summary>
        public int FontCharset { get; }

        private readonly Dictionary<string, ImageInfo> m_imageCache = new Dictionary<string, ImageInfo>();

        public SemanticModel(VisualStyle style)
        {
            Style = style;
            Platform = style.Platform;

            foreach (var cls in style.Classes.Values.OrderBy(c => c.ClassId))
            {
                if (ClassesByName.ContainsKey(cls.ClassName))
                {
                    DuplicateClassNames.Add(cls.ClassName);
                    continue;
                }
                ClassesByName[cls.ClassName] = cls;
            }

            FontCharset = ReadFontCharset(style);

            foreach (var cls in style.Classes.Values.OrderBy(c => c.ClassId))
            {
                var partNames = PartNames(cls.ClassName, Platform);
                foreach (var part in cls.Parts.Values.OrderBy(p => p.PartId))
                {
                    bool known = partNames.TryGetValue(part.PartId, out string partName);
                    if (!known) partName = "Part " + part.PartId;

                    foreach (var state in part.States.Values.OrderBy(s => s.StateId))
                    {
                        var seen = new Dictionary<string, int>();
                        foreach (var prop in state.Properties)
                        {
                            var e = new SemanticEntry
                            {
                                ClassName = cls.ClassName,
                                ClassId = cls.ClassId,
                                PartName = partName,
                                PartId = part.PartId,
                                PartNameKnown = known,
                                // state ids are kept numeric: the editor's state tables are generic per part
                                // and several are documented as "might not match every part".
                                StateName = "S" + state.StateId,
                                StateId = state.StateId,
                                NameId = prop.Header.nameID,
                                PropertyName = PropertyName(prop.Header.nameID),
                                TypeId = prop.Header.typeID,
                                Property = prop,
                            };
                            string baseKey = e.StateKey + "|" + e.PropertyName + "#" + e.TypeId;
                            seen.TryGetValue(baseKey, out int n);
                            e.Occurrence = n;
                            seen[baseKey] = n + 1;

                            e.RawValue = FormatValue(prop);
                            e.Value = e.RawValue;
                            var res = ResourceRef(prop, Platform);
                            if (res != null)
                            {
                                e.ResourceType = res.Item1;
                                e.ResourceId = res.Item2;
                                var info = GetImageInfo(res.Item1, res.Item2);
                                e.Value = res.Item1 + ":" + (info == null ? "missing#" + res.Item2
                                    : info.Decodable ? "px:" + info.PixelSha1 : "bytes:" + info.ByteSha1);
                            }
                            else if (IsStringRef(prop, Platform))
                            {
                                e.IsStringRef = true;
                                // FONT and non-image FILENAME records carry a string-table id (full dword).
                                string text = ResolveString(prop.Header.shortFlag);
                                e.Value = text != null ? "str:" + text : "strid:" + prop.Header.shortFlag;
                            }

                            Entries.Add(e);
                            if (!ByKey.ContainsKey(e.Key)) ByKey[e.Key] = e;
                        }
                    }
                }
            }
        }

        public static Dictionary<int, string> PartNames(string className, Platform platform)
        {
            var map = new Dictionary<int, string>();
            foreach (var p in VisualStyleParts.Find(className, platform))
            {
                if (!map.ContainsKey(p.Id)) map[p.Id] = p.Name;
            }
            return map;
        }

        public static string PropertyName(int nameId)
        {
            if (VisualStyleProperties.PROPERTY_INFO_MAP.TryGetValue(nameId, out var info))
                return info.Name;
            return "PROP" + nameId;
        }

        // IMAGEFILE..IMAGEFILE7 and GLYPHIMAGEFILE. uxtheme picks the handler by property and type:
        // only these load an IMAGE resource, any other FILENAME is a string id. IMAGEFILE6/7
        // (3009/3010) only became image properties after win7: uxtheme 6.1.7600 still reads them
        // as string ids (GetThemePrimitiveID table 0x7FF72F21F00), 19041 as images.
        private static readonly HashSet<int> ImageFileIds = new HashSet<int> { 3001, 3002, 3003, 3004, 3005, 3006, 3008, 3009, 3010, 5144, 5145, 5146 };
        private static readonly HashSet<int> ImageFileIdsWin7 = new HashSet<int> { 3001, 3002, 3003, 3004, 3005, 3006, 3008 };

        private static bool IsImageFileId(int nameId, Platform platform)
        {
            // XP adds STOCKIMAGEFILE (3007); the loader turned all of them into in-memory IMAGE ids.
            if (platform == Platform.WinXP) return VisualStyle.XpImageIds.Contains(nameId);
            return platform <= Platform.Win7 ? ImageFileIdsWin7.Contains(nameId) : ImageFileIds.Contains(nameId);
        }

        /// <summary>(resource type, id) when the property points at an IMAGE or STREAM resource.</summary>
        public static Tuple<string, int> ResourceRef(StyleProperty prop, Platform platform = Platform.Win10)
        {
            // the loaders use only the low word of the external id as the resource name.
            int id = prop.Header.shortFlag & 0xFFFF;
            switch ((IDENTIFIER)prop.Header.typeID)
            {
                case IDENTIFIER.FILENAME:
                case IDENTIFIER.FILENAME_LITE:
                    return IsImageFileId(prop.Header.nameID, platform) ? Tuple.Create("IMAGE", id) : null;
                case IDENTIFIER.DISKSTREAM:
                    return Tuple.Create("STREAM", id);
                case IDENTIFIER.STREAM:
                    // inline payloads have external id 0 and are compared as plain values.
                    return prop.Header.shortFlag != 0 ? Tuple.Create("STREAM", id) : null;
                default:
                    return null;
            }
        }

        public static bool IsStringRef(StyleProperty prop, Platform platform = Platform.Win10)
        {
            return prop.Header.typeID == (int)IDENTIFIER.FONT
                // a STRING with an external id is LoadStringRes'd from the string table (both 7600 and 19041).
                || (prop.Header.typeID == (int)IDENTIFIER.STRING && prop.Header.shortFlag != 0)
                || ((prop.Header.typeID == (int)IDENTIFIER.FILENAME || prop.Header.typeID == (int)IDENTIFIER.FILENAME_LITE)
                    && !IsImageFileId(prop.Header.nameID, platform));
        }

        /// <summary>resources that must decode as png: images and the DWM atlas. STREAM (214) blobs are raw bytes.</summary>
        public static bool MustBePng(StyleProperty prop)
        {
            return prop.Header.typeID != (int)IDENTIFIER.STREAM;
        }

        public byte[] GetResourceBytes(string type, int id)
        {
            return Style.GetRawResource(type, id);
        }

        /// <summary>null when the resource doesn't exist.</summary>
        public ImageInfo GetImageInfo(string type, int id)
        {
            string k = type + "/" + id;
            if (m_imageCache.TryGetValue(k, out var info)) return info;
            var data = Style.GetRawResource(type, id);
            info = data == null ? null : ImageIdentity.Analyze(data);
            m_imageCache[k] = info;
            return info;
        }

        /// <summary>String table lookup over every loaded language; null if the id isn't there.</summary>
        public string ResolveString(int id)
        {
            if (Style.PreferredStringTable != null && Style.PreferredStringTable.TryGetValue(id, out var s)) return s;
            foreach (var t in Style.StringTables.OrderBy(t => t.Key))
            {
                if (t.Value.TryGetValue(id, out s)) return s;
            }
            return null;
        }

        /// <summary>
        /// Property lookup with uxtheme's fallback order: the state, state 0 of the part, part 0
        /// state 0, then the base class with the same part/state, then globals.
        /// (uxtheme 19041 property lookup, see docs/win7-importer.md.)
        /// </summary>
        public static StyleProperty FindEffective(VisualStyle style, StyleClass cls, int part, int state, IDENTIFIER name)
        {
            var seen = new HashSet<int>();
            for (var c = cls; c != null && seen.Add(c.ClassId);)
            {
                var p = FindLocal(c, part, state, name) ?? FindLocal(c, part, 0, name) ?? FindLocal(c, 0, 0, name);
                if (p != null) return p;
                c = c.BaseClassId >= 0 && style.Classes.TryGetValue(c.BaseClassId, out var b) ? b : null;
            }
            var globals = style.Classes.Values.FirstOrDefault(c => string.Equals(c.ClassName, "globals", StringComparison.OrdinalIgnoreCase));
            return globals == null || seen.Contains(globals.ClassId) ? null : FindLocal(globals, 0, 0, name);
        }

        private static StyleProperty FindLocal(StyleClass cls, int part, int state, IDENTIFIER name)
        {
            if (!cls.Parts.TryGetValue(part, out var p) || !p.States.TryGetValue(state, out var s)) return null;
            return s.Properties.FirstOrDefault(x => x.Header.nameID == (int)name);
        }

        /// <summary>
        /// The [globals] Charset record (TMT_CHARSET, 403) as an int, DEFAULT_CHARSET when the style
        /// has none. XP only accepts it in [globals] before the first font, so that is the only place
        /// it is looked for here too.
        /// </summary>
        private static int ReadFontCharset(VisualStyle style)
        {
            var globals = style.Classes.Values.FirstOrDefault(c => string.Equals(c.ClassName, "globals", StringComparison.OrdinalIgnoreCase));
            if (globals == null) return 1;
            var prop = FindLocal(globals, 0, 0, IDENTIFIER.CHARSET);
            if (prop == null) return 1;
            try
            {
                return Convert.ToInt32(prop.GetValue(), CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 1;
            }
        }

        /// <summary>
        /// The text a FONT property's string should be written as: the source text, plus the style's
        /// charset when it isn't DEFAULT_CHARSET, so the imported font keeps the charset XP applied
        /// (themeldr ParseFontValue stamps _uCharSet into every LOGFONT it builds).
        /// </summary>
        public string ResolveFontText(StyleProperty prop)
        {
            string text = ResolveString(prop.Header.shortFlag);
            if (text == null || FontCharset == 1) return text;
            return text.Trim() + ", Charset:" + FontCharset.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>first string id whose text matches exactly, then ignoring case. -1 if none.</summary>
        public int FindStringId(string text, out bool exact)
        {
            exact = false;
            var all = Style.StringTables.OrderBy(t => t.Key).SelectMany(t => t.Value).OrderBy(kv => kv.Key).ToList();
            foreach (var kv in all)
            {
                if (string.Equals(kv.Value, text, StringComparison.Ordinal)) { exact = true; return kv.Key; }
            }
            foreach (var kv in all)
            {
                if (string.Equals(kv.Value, text, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            }
            return -1;
        }

        public static string FormatValue(StyleProperty prop)
        {
            object v = prop.GetValue();
            var ci = CultureInfo.InvariantCulture;
            switch ((IDENTIFIER)prop.Header.typeID)
            {
                case IDENTIFIER.INTLIST:
                    return "[" + string.Join(",", ((List<int>)v).Select(i => i.ToString(ci))) + "]";
                case IDENTIFIER.COLORLIST:
                    return "[" + string.Join(",", ((List<Color>)v).Select(c => c.ToArgb().ToString("X8"))) + "]";
                case IDENTIFIER.STRING:
                    return "\"" + (string)v + "\"";
                case IDENTIFIER.COLOR:
                    {
                        var c = (Color)v;
                        return $"rgb({c.R},{c.G},{c.B})";
                    }
                case IDENTIFIER.POSITION:
                    {
                        var s = (Size)v;
                        return $"({s.Width},{s.Height})";
                    }
                case IDENTIFIER.RECTTYPE:
                case IDENTIFIER.MARGINS:
                    {
                        var m = (Margins)v;
                        return $"(l{m.Left},t{m.Top},r{m.Right},b{m.Bottom})";
                    }
                case IDENTIFIER.BOOLTYPE:
                    return (bool)v ? "true" : "false";
                case IDENTIFIER.FILENAME:
                case IDENTIFIER.FILENAME_LITE:
                case IDENTIFIER.DISKSTREAM:
                case IDENTIFIER.FONT:
                case IDENTIFIER.INT:
                case IDENTIFIER.SIZE:
                case IDENTIFIER.ENUM:
                case IDENTIFIER.HIGHCONTRASTCOLORTYPE:
                    return Convert.ToString(v, ci) + (prop.Header.shortFlag != 0 && !prop.IsImageProperty() && prop.Header.typeID != (int)IDENTIFIER.FONT ? $"(sf{prop.Header.shortFlag})" : "");
                default:
                    {
                        var b = v as byte[];
                        return "raw:" + (b == null ? "sf" + prop.Header.shortFlag : BitConverter.ToString(b).Replace("-", ""));
                    }
            }
        }
    }
}
