using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace libmsstyle.Import.Dui
{
    /// <summary>
    /// Offline check of the DUIXML a ported shellstyle ships. The porter rewrites shellstyle markup with string
    /// surgery and a translator, and a port that parses as XML can still stop Explorer's DirectUI parser at window
    /// creation ("DUIXMLParserFailure ... unable to load graphics", a black folder window). Everything here is the
    /// subset of the grammar that is decidable from the resource text alone: document shape, stylesheet identity,
    /// layout constructors and their arity, <c>layoutpos</c> tokens, value-function arity, ids, and the graphic ids
    /// the module can actually resolve. It does not know class property surfaces, so it can't prove a property name
    /// is valid for its tag; it proves the document is structurally loadable.
    /// </summary>
    public static class DuiValidator
    {
        /// <summary>Registered layout constructors (dui70) and the argument counts each accepts.</summary>
        private static readonly Dictionary<string, (int Min, int Max)> Layouts = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["borderlayout"] = (0, 0),
            ["filllayout"] = (0, 0),
            ["shellborderlayout"] = (0, 0),
            ["gridlayout"] = (2, 2),
            ["rowlayout"] = (0, 3),
            ["flowlayout"] = (0, 4),
            ["verticalflowlayout"] = (0, 4),
            ["tablelayout"] = (0, 16),
            ["ninegridlayout"] = (0, 16),
        };

        /// <summary>Value functions with a fixed or bounded argument count. <c>graphic</c>/<c>gradient</c>/<c>icon</c>
        /// and friends are variable and only checked for balance and for the ids they name.</summary>
        private static readonly Dictionary<string, (int Min, int Max)> Functions = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["rect"] = (4, 4),
            ["size"] = (2, 2),
            ["point"] = (2, 2),
            ["rgb"] = (3, 3),
            ["argb"] = (4, 4),
            ["atom"] = (1, 1),
            ["library"] = (1, 1),
            ["resstr"] = (1, 2),
            ["sysmetric"] = (1, 1),
            ["sysmetricstr"] = (1, 1),
            ["themeable"] = (2, 2),
            ["dtb"] = (3, 3),
            ["dfc"] = (2, 2),
            ["gtc"] = (4, 4),
            ["gtf"] = (3, 3),
            ["gtmar"] = (4, 4),
            ["gtmet"] = (4, 4),
            ["gtps"] = (3, 3),
            ["ressheet"] = (2, 3),
        };

        /// <summary>Constructors whose exact signature isn't public; only the parens and the ids they name are checked.</summary>
        private static readonly HashSet<string> VariableFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gradient", "graphic", "sgraphic", "resbmp", "icon", "timingfunction", "doublelist", "res",
        };

        private static readonly HashSet<string> LayoutPositions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "auto", "absolute", "none", "left", "top", "right", "bottom", "client",
        };

        /// <summary>One problem found in one UIFILE resource.</summary>
        public sealed class Finding
        {
            public string Resource { get; set; }
            public int Line { get; set; }
            public string Message { get; set; }
            public override string ToString() => $"DUIXML {Resource}: {Message}" + (Line > 0 ? $" (line {Line})" : "");
        }

        public sealed class Result
        {
            public List<Finding> Errors { get; } = new List<Finding>();
            public List<Finding> Warnings { get; } = new List<Finding>();
            /// <summary>UIFILE resources that were read and parsed.</summary>
            public int Documents { get; set; }
        }

        /// <summary>
        /// Validates every DUIXML resource of a shellstyle module. <paramref name="bitmapIds"/> is what
        /// <c>graphic(id, …)</c>/<c>resbmp(id, …)</c> can resolve in that module; pass null to skip that check.
        /// </summary>
        public static Result ValidateModule(string shellStylePath, ISet<int> bitmapIds = null)
        {
            if (shellStylePath == null) throw new ArgumentNullException(nameof(shellStylePath));
            var result = new Result();
            IntPtr mod = Win32Api.LoadLibraryEx(shellStylePath, IntPtr.Zero, Win32Api.LoadLibraryFlags.LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE);
            if (mod == IntPtr.Zero) throw new IOException($"can't open \"{shellStylePath}\"");
            try
            {
                var docs = new List<(string name, string text)>();
                foreach (var type in new[] { "UIFILE", "SHELL32_UIFILE" })
                    foreach (uint id in ResourceIds(mod, type))
                        foreach (var (_, text, _) in TextResources(mod, type, id))
                            docs.Add((type + " " + id, text));
                result.Documents = docs.Count;
                Validate(docs, bitmapIds, result);
            }
            finally { Win32Api.FreeLibrary(mod); }
            return result;
        }

        /// <summary>Validates a set of named DUIXML documents as one module (stylesheets resolve across resources).</summary>
        public static void Validate(IEnumerable<(string name, string text)> documents, ISet<int> bitmapIds, Result result)
        {
            var docs = documents.ToList();
            var resids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sheetRefs = new List<(string doc, string sheet)>();

            foreach (var (name, text) in docs)
            {
                XDocument doc;
                try
                {
                    doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
                }
                catch (XmlException e)
                {
                    Error(result, name, e.LineNumber, "not well-formed XML: " + e.Message);
                    continue;
                }
                var root = doc.Root;
                if (root == null || !string.Equals(root.Name.LocalName, "duixml", StringComparison.OrdinalIgnoreCase))
                {
                    Error(result, name, Line(root), "root element is not <duixml>");
                    continue;
                }

                // stylesheets: every <style> needs its resid, and two sheets with one name make the reference
                // ambiguous (DirectUI keeps whichever it parsed first).
                foreach (var style in root.Descendants().Where(e => string.Equals(e.Name.LocalName, "style", StringComparison.OrdinalIgnoreCase)))
                {
                    string resid = (string)DuiXmlDocument.Attribute(style, "resid");
                    if (string.IsNullOrWhiteSpace(resid))
                    {
                        Error(result, name, Line(style), "<style> without a resid attribute");
                        continue;
                    }
                    if (resids.TryGetValue(resid, out string first))
                        Error(result, name, Line(style), $"duplicate stylesheet resid \"{resid}\" (already defined in {first})");
                    else
                        resids[resid] = name;
                }

                foreach (var e in root.Descendants())
                {
                    CheckElement(result, name, e, bitmapIds);
                    foreach (var attr in new[] { "sheet", "base" })
                    {
                        string v = (string)DuiXmlDocument.Attribute(e, attr);
                        if (v == null) continue;
                        // ressheet(...) names an external module's sheet; only a plain name resolves in this module.
                        if (v.IndexOf('(') >= 0) continue;
                        sheetRefs.Add((name, v.Trim()));
                    }
                }

                // every attribute value is an expression: unbalanced parens are what a truncated string surgery
                // leaves behind, and the parser rejects the whole document for it.
                foreach (Match m in Regex.Matches(text, "=\\s*\"([^\"]*)\""))
                {
                    string v = m.Groups[1].Value;
                    if (Count(v, '(') != Count(v, ')'))
                        Error(result, name, LineAt(text, m.Index), $"unbalanced parentheses in \"{Truncate(v)}\"");
                }
            }

            // a sheet reference that resolves nowhere in the module fails the parse of the element carrying it.
            // "common" and the two *placeholder sheets are written by the shell itself (shell32 registers them),
            // so they are not errors; anything else must be defined here.
            foreach (var (doc, sheet) in sheetRefs)
                if (!resids.ContainsKey(sheet) && !ShellProvidedSheets.Contains(sheet))
                    Error(result, doc, 0, $"sheet reference \"{sheet}\" resolves to no stylesheet in the module");
        }

        /// <summary>Sheets the shell registers before loading the theme's markup, so a reference to them is valid
        /// even though no UIFILE defines them.</summary>
        private static readonly HashSet<string> ShellProvidedSheets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "common", "splitbuttonstyleplaceholder", "taskbuttonstyleplaceholder",
        };

        private static void CheckElement(Result result, string doc, XElement e, ISet<int> bitmapIds)
        {
            int line = Line(e);
            string tag = e.Name.LocalName;

            string layout = (string)DuiXmlDocument.Attribute(e, "layout");
            if (layout != null) CheckLayout(result, doc, line, tag, layout);

            string pos = (string)DuiXmlDocument.Attribute(e, "layoutpos");
            if (pos != null && !LayoutPositions.Contains(pos.Trim()))
                Error(result, doc, line, $"<{tag}> has layoutpos=\"{pos}\", which is not a placement token");

            string id = (string)DuiXmlDocument.Attribute(e, "id");
            if (id != null && !Regex.IsMatch(id.Trim(), "^atom\\([^()]*\\)$", RegexOptions.IgnoreCase))
                Error(result, doc, line, $"<{tag}> has id=\"{id}\"; an id must be atom(name)");

            // A rowlayout() with an explicit height lays its children out at zero size: DirectUI skips DoLayout for
            // them, so the row's items disappear without any parse error (the Control Panel band's links did).
            if (layout != null && Regex.IsMatch(layout.Trim(), "^rowlayout\\s*\\(\\s*\\)$", RegexOptions.IgnoreCase)
                && DuiXmlDocument.Attribute(e, "height") != null)
                Warn(result, doc, line, $"<{tag}> is a rowlayout() with a fixed height; its children are laid out at zero size and don't show");

            foreach (var attr in e.Attributes())
            {
                if (attr.IsNamespaceDeclaration) continue;
                CheckExpressions(result, doc, line, tag, attr.Value, bitmapIds);
            }
        }

        private static void CheckLayout(Result result, string doc, int line, string tag, string value)
        {
            var m = Regex.Match(value.Trim(), "^([A-Za-z_]\\w*)\\s*(?:\\((.*)\\))?$", RegexOptions.Singleline);
            if (!m.Success)
            {
                Error(result, doc, line, $"<{tag}> has layout=\"{Truncate(value)}\", which is not a layout constructor");
                return;
            }
            string name = m.Groups[1].Value;
            if (!Layouts.TryGetValue(name, out var arity))
            {
                Error(result, doc, line, $"<{tag}> has layout=\"{name}\", which is not a registered layout constructor");
                return;
            }
            int count = m.Groups[2].Success ? SplitArgs(m.Groups[2].Value).Count : 0;
            if (count < arity.Min || count > arity.Max)
                Error(result, doc, line, $"<{tag}> has layout=\"{name}\" with {count} argument(s); it takes {Range(arity)}");
        }

        /// <summary>Checks every function call in one attribute value: known name, argument count, and the bitmap ids
        /// a graphic constructor names.</summary>
        private static void CheckExpressions(Result result, string doc, int line, string tag, string value, ISet<int> bitmapIds)
        {
            if (value.IndexOf('(') < 0) return;
            foreach (Match m in Regex.Matches(value, "\\b([A-Za-z_]\\w*)\\s*\\("))
            {
                string name = m.Groups[1].Value;
                int open = m.Index + m.Length - 1;
                int close = MatchParen(value, open);
                if (close < 0) continue; // reported by the balance check
                string inner = value.Substring(open + 1, close - open - 1);
                if (Layouts.ContainsKey(name)) continue; // checked by CheckLayout

                if (Functions.TryGetValue(name, out var arity))
                {
                    int count = string.IsNullOrWhiteSpace(inner) ? 0 : SplitArgs(inner).Count;
                    if (count < arity.Min || count > arity.Max)
                        Error(result, doc, line, $"<{tag}>: {name}() takes {Range(arity)} argument(s), got {count}");
                }
                else if (VariableFunctions.Contains(name))
                {
                    if (bitmapIds != null && (name.Equals("graphic", StringComparison.OrdinalIgnoreCase) || name.Equals("resbmp", StringComparison.OrdinalIgnoreCase)))
                    {
                        var idm = Regex.Match(inner, "^\\s*(\\d+)");
                        if (idm.Success && int.TryParse(idm.Groups[1].Value, out int id) && !bitmapIds.Contains(id))
                            Error(result, doc, line, $"<{tag}>: {name}({id}, …) names bitmap {id}, which the module doesn't contain");
                    }
                }
                else
                {
                    Error(result, doc, line, $"<{tag}>: \"{name}\" is not a known DUIXML function");
                }
            }
        }

        private static int MatchParen(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; ++i)
            {
                if (s[i] == '(') ++depth;
                else if (s[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static List<string> SplitArgs(string s)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; ++i)
            {
                if (s[i] == '(') ++depth;
                else if (s[i] == ')') --depth;
                else if (s[i] == ',' && depth == 0) { parts.Add(s.Substring(start, i - start)); start = i + 1; }
            }
            if (start < s.Length) parts.Add(s.Substring(start));
            return parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        }

        private static int Count(string s, char c) => s.Count(x => x == c);

        private static string Range((int Min, int Max) a) => a.Min == a.Max ? a.Min.ToString() : a.Min + "-" + a.Max;

        private static string Truncate(string s) => s.Length <= 60 ? s : s.Substring(0, 57) + "...";

        private static int Line(XElement e) => (e as IXmlLineInfo)?.HasLineInfo() == true ? ((IXmlLineInfo)e).LineNumber : 0;

        private static int LineAt(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; ++i) if (text[i] == '\n') ++line;
            return line;
        }

        private static void Error(Result r, string doc, int line, string msg) => r.Errors.Add(new Finding { Resource = doc, Line = line, Message = msg });
        private static void Warn(Result r, string doc, int line, string msg) => r.Warnings.Add(new Finding { Resource = doc, Line = line, Message = msg });

        private static List<uint> ResourceIds(IntPtr mod, string type)
        {
            var ids = new List<uint>();
            var typePtr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(type);
            try
            {
                Win32Api.EnumResourceNamesEx(mod, typePtr, (m, t, name, l) =>
                {
                    long v = name.ToInt64();
                    if (v > 0 && v <= 0xFFFF) ids.Add((uint)v);
                    return true;
                }, IntPtr.Zero, Win32Api.EnumResourceFlags.RESOURCE_ENUM_LN, 0);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(typePtr); }
            return ids;
        }

        private static List<(ushort lang, string text, System.Text.Encoding encoding)> TextResources(IntPtr mod, string type, uint id)
        {
            var langs = new List<ushort>();
            Win32Api.EnumResourceLanguagesEx(mod, type, "#" + id, (m, t, n, lang, l) => { langs.Add(lang); return true; },
                IntPtr.Zero, Win32Api.EnumResourceFlags.RESOURCE_ENUM_LN, 0);
            var list = new List<(ushort, string, System.Text.Encoding)>();
            foreach (var lang in langs)
            {
                IntPtr r = Win32Api.FindResourceEx(mod, type, id, lang);
                if (r == IntPtr.Zero) continue;
                uint size = Win32Api.SizeofResource(mod, r);
                var buf = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(Win32Api.LockResource(Win32Api.LoadResource(mod, r)), buf, 0, (int)size);
                bool utf16 = size >= 2 && buf[0] == 0xFF && buf[1] == 0xFE;
                var encoding = utf16 ? System.Text.Encoding.Unicode : System.Text.Encoding.Default;
                list.Add((lang, utf16 ? encoding.GetString(buf, 2, buf.Length - 2) : encoding.GetString(buf), encoding));
            }
            return list;
        }
    }
}
