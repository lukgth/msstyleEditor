using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace libmsstyle.Import.Dui
{
    /// <summary>
    /// Syntax tree for XP's DUI stylesheets (the UIFILE 1 resource of shellstyle.dll). XP's parser is a yacc
    /// grammar over a stylesheet of <c>&lt;style resid=name&gt; selector { prop: value; }&lt;/style&gt;</c> blocks;
    /// this is a scanner + recursive descent parser for the same language, so selectors and declarations are
    /// read structurally instead of by regular expression. Malformed input is reported (with line numbers)
    /// instead of silently dropped.
    /// </summary>
    public static class XpUifileSyntax
    {
        public sealed class Diagnostics
        {
            public readonly List<string> Messages = new List<string>();
            public void Add(string message) { Messages.Add(message); }
        }

        // ---- values ---------------------------------------------------------------------------

        public enum ValueKind { Function, String, Number, Hex, Ident, Binary, Raw }

        /// <summary>One value expression: <c>rect(1rp,0rp,2rp,0rp)</c>, <c>rcbmp(110,6,#FF00FF,0,0,1,0)</c>, <c>a | b</c>.</summary>
        public class Value
        {
            public ValueKind Kind;
            public string Name;                  // Function name / Ident text / operator for Binary
            public string Text;                  // Number/Hex/Ident tokens, String contents (unquoted), Raw text
            public char Quote;                   // String quote character (' or ")
            public List<Value> Args = new List<Value>();   // Function args
            public Value Left, Right;            // Binary operands
            public string Raw;                   // source text, verbatim (trimmed)

            public bool IsFunction(string name) { return Kind == ValueKind.Function && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase); }

            public override string ToString()
            {
                if (Raw != null) return Raw;
                if (Kind == ValueKind.Function)
                {
                    var sb = new StringBuilder(Name).Append('(');
                    for (int i = 0; i < Args.Count; i++) { if (i != 0) sb.Append(", "); sb.Append(Args[i]); }
                    return sb.Append(')').ToString();
                }
                if (Kind == ValueKind.Binary) return Left + " " + Name + " " + Right;
                if (Kind == ValueKind.String) return (Quote == 0 ? '\'' : Quote) + Text + (Quote == 0 ? '\'' : Quote);
                return Text;
            }
        }

        /// <summary>Parse a declaration value. A value statement can be a comma separated list at top level
        /// (<c>rectanglev | s | fast</c>, <c>a, b</c>).</summary>
        public static Value ParseValue(string text)
        {
            if (text == null) return null;
            var p = new ValueParser(text);
            var v = p.ParseExpression();
            // XP writes comma lists as independent values (e.g. `a, b`); keep the tail for callers that care.
            if (!p.AtEnd)
            {
                var raw = new Value { Kind = ValueKind.Raw, Raw = text.Trim(), Text = text.Trim() };
                return raw;
            }
            return v;
        }

        internal sealed class ValueParser
        {
            private readonly string m_s;
            private int m_i;
            public ValueParser(string s) { m_s = s ?? ""; m_i = 0; }
            public bool AtEnd { get { SkipWs(); return m_i >= m_s.Length; } }

            private void SkipWs()
            {
                while (m_i < m_s.Length && char.IsWhiteSpace(m_s[m_i])) m_i++;
            }

            public Value ParseExpression()
            {
                var left = ParsePrimary();
                SkipWs();
                while (m_i < m_s.Length && (m_s[m_i] == '|' || m_s[m_i] == '+'))
                {
                    char op = m_s[m_i++];
                    var right = ParsePrimary();
                    left = new Value { Kind = ValueKind.Binary, Name = op.ToString(), Left = left, Right = right };
                    SkipWs();
                }
                return left;
            }

            private Value ParsePrimary()
            {
                SkipWs();
                if (m_i >= m_s.Length) return Raw();
                char c = m_s[m_i];
                if (c == '\'' || c == '"') return ParseString(c);
                if (c == '#') return ParseHex();
                if (c == '-' || c == '+' || char.IsDigit(c)) return ParseNumber();
                if (IsIdentStart(c)) return ParseIdentOrCall();
                return Raw();
            }

            private static bool IsIdentStart(char c) { return char.IsLetter(c) || c == '_' || c == '%' || c == '@' || c == '$'; }
            private static bool IsIdentChar(char c) { return char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '%' || c == '@' || c == '$' || c == ':'; }

            private Value ParseIdentOrCall()
            {
                int start = m_i;
                while (m_i < m_s.Length && IsIdentChar(m_s[m_i])) m_i++;
                string name = m_s.Substring(start, m_i - start);
                SkipWs();
                if (m_i < m_s.Length && m_s[m_i] == '(')
                {
                    m_i++;
                    var fn = new Value { Kind = ValueKind.Function, Name = name, Raw = null };
                    SkipWs();
                    if (m_i < m_s.Length && m_s[m_i] == ')') { m_i++; return fn; }
                    while (m_i < m_s.Length)
                    {
                        fn.Args.Add(ParseExpression());
                        SkipWs();
                        if (m_i >= m_s.Length) break;
                        if (m_s[m_i] == ',') { m_i++; continue; }
                        if (m_s[m_i] == ')') { m_i++; break; }
                        // Not a comma: take the rest literally so a malformed argument list doesn't lose data.
                        fn.Args.Add(Raw());
                        break;
                    }
                    return fn;
                }
                return new Value { Kind = ValueKind.Ident, Text = name, Raw = name };
            }

            private Value ParseString(char quote)
            {
                int start = m_i++;
                var sb = new StringBuilder();
                while (m_i < m_s.Length)
                {
                    char c = m_s[m_i];
                    if (c == quote)
                    {
                        // '' inside a string is an escaped quote in some packs; consume pairs.
                        if (m_i + 1 < m_s.Length && m_s[m_i + 1] == quote) { sb.Append(quote); m_i += 2; continue; }
                        m_i++;
                        var str = new Value { Kind = ValueKind.String, Text = sb.ToString(), Quote = quote, Raw = m_s.Substring(start, m_i - start) };
                        return str;
                    }
                    sb.Append(c); m_i++;
                }
                return new Value { Kind = ValueKind.String, Text = sb.ToString(), Quote = quote, Raw = m_s.Substring(start) };
            }

            private Value ParseHex()
            {
                int start = m_i++;
                while (m_i < m_s.Length && Uri.IsHexDigit(m_s[m_i])) m_i++;
                string t = m_s.Substring(start, m_i - start);
                return new Value { Kind = ValueKind.Hex, Text = t, Raw = t };
            }

            private Value ParseNumber()
            {
                int start = m_i;
                if (m_s[m_i] == '-' || m_s[m_i] == '+') m_i++;
                while (m_i < m_s.Length && (char.IsDigit(m_s[m_i]) || m_s[m_i] == '.')) m_i++;
                // units: rp, px, pt, em, %, deg...
                while (m_i < m_s.Length && (char.IsLetter(m_s[m_i]) || m_s[m_i] == '%')) m_i++;
                string t = m_s.Substring(start, m_i - start);
                return new Value { Kind = ValueKind.Number, Text = t, Raw = t };
            }

            private Value Raw()
            {
                int start = m_i;
                int depth = 0;
                while (m_i < m_s.Length)
                {
                    char c = m_s[m_i];
                    if (c == '(') depth++;
                    else if (c == ')') { if (depth == 0) break; depth--; }
                    else if (c == ',' && depth == 0) break;
                    else if (c == '|' && depth == 0) break;
                    m_i++;
                }
                var t = m_s.Substring(start, m_i - start).Trim();
                return new Value { Kind = ValueKind.Raw, Raw = t, Text = t };
            }
        }

        // ---- selectors ---------------------------------------------------------------------

        /// <summary>One selector condition: <c>[id=atom(header)]</c>, <c>[keyfocused]</c>, <c>[selected=false]</c>.</summary>
        public sealed class Condition
        {
            public string Name;             // lower case
            public string Text;             // raw condition value (unquoted)
            public Value Value;             // parsed condition value (null for a bare condition)
            public bool Negated;            // written as [!name] / [name=false]
            public bool IsId { get { return Name == "id"; } }
            /// <summary>the atom name for [id=atom(x)] (x as written, original case).</summary>
            public string Atom
            {
                get
                {
                    if (!IsId || Value == null || !Value.IsFunction("atom") || Value.Args.Count != 1) return null;
                    var a = Value.Args[0];
                    return a.Text;
                }
            }
        }

        public sealed class Selector
        {
            public string Class;            // as written; matching is case-insensitive
            public List<Condition> Conditions = new List<Condition>();
            public string Raw;              // normalized source text

            /// <summary>the [id=atom(x)] condition, or null.</summary>
            public Condition IdCondition
            {
                get { for (int i = 0; i < Conditions.Count; i++) if (Conditions[i].IsId) return Conditions[i]; return null; }
            }
            public string Id { get { var c = IdCondition; return c == null ? null : c.Text; } }
            public string AtomId { get { var c = IdCondition; return c == null ? null : c.Atom; } }
            /// <summary>state conditions only (id excluded), for the cascade.</summary>
            public IEnumerable<Condition> StateConditions
            {
                get { foreach (var c in Conditions) if (!c.IsId) yield return c; }
            }
            public override string ToString() { return Raw ?? Class; }
        }

        /// <summary>Parse one selector string (<c>button [id=atom(header)][keyfocused]</c>).</summary>
        public static Selector ParseSelector(string text, Diagnostics diag = null, int line = 0)
        {
            string s = text == null ? "" : text.Trim();
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '[') i++;
            string cls = s.Substring(start, i - start);
            if (cls.Length == 0) { if (diag != null) diag.Add(Where(line) + "selector has no class: \"" + s + "\""); return null; }
            var sel = new Selector { Class = cls, Raw = NormalizeSelector(s) };
            while (true)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;
                if (s[i] != '[')
                {
                    // Not a condition (e.g. a descendant selector, which XP's GDI shellstyle doesn't use).
                    if (diag != null) diag.Add(Where(line) + "unsupported selector text after class: \"" + s.Substring(i) + "\"");
                    sel.Conditions.Add(new Condition { Name = "selector", Text = s.Substring(i).Trim(), Value = new Value { Kind = ValueKind.Raw, Raw = s.Substring(i).Trim() } });
                    break;
                }
                int close = FindClosingBracket(s, i);
                if (close < 0) { if (diag != null) diag.Add(Where(line) + "unterminated condition in selector: \"" + s + "\""); break; }
                string body = s.Substring(i + 1, close - i - 1);
                int eq = FindTopLevel(body, '=');
                string name = (eq < 0 ? body : body.Substring(0, eq)).Trim();
                var cond = new Condition { Name = name.ToLowerInvariant() };
                if (eq >= 0)
                {
                    // [name != value] is XP's PALOGOP_NotEqual (parse.y:559): the '!' belongs to the operator, not
                    // to the property name.
                    if (cond.Name.EndsWith("!", StringComparison.Ordinal))
                    {
                        cond.Name = cond.Name.Substring(0, cond.Name.Length - 1).Trim();
                        cond.Negated = true;
                    }
                    string val = body.Substring(eq + 1).Trim();
                    if (val.Length >= 2 && (val[0] == '\'' || val[0] == '"') && val[val.Length - 1] == val[0]) val = val.Substring(1, val.Length - 2);
                    cond.Text = val;
                    cond.Value = ParseValue(val);
                }
                else if (cond.Name.StartsWith("!", StringComparison.Ordinal))
                {
                    // a bare negated flag, `[!keyfocused]`: the '!' is the operator, and the condition matches when
                    // the state is false. Without this the name kept its '!' and no state ever resolved.
                    cond.Name = cond.Name.Substring(1).Trim().ToLowerInvariant();
                    cond.Negated = true;
                }
                if (cond.Name.Length == 0) { if (diag != null) diag.Add(Where(line) + "empty condition in selector: \"" + s + "\""); }
                else sel.Conditions.Add(cond);
                i = close + 1;
            }
            return sel;
        }

        private static string Where(int line) { return line > 0 ? "line " + line + ": " : ""; }

        internal static string NormalizeSelector(string s)
        {
            var sb = new StringBuilder();
            bool ws = false;
            foreach (char c in (s ?? "").Trim())
            {
                if (char.IsWhiteSpace(c)) { ws = true; continue; }
                if (ws && c != '[') sb.Append(' ');
                ws = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static int FindClosingBracket(string s, int open)
        {
            char quote = '\0';
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != 0) { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static int FindTopLevel(string s, char want)
        {
            int depth = 0; char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != 0) { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == want && depth == 0) return i;
            }
            return -1;
        }

        // ---- declarations ----------------------------------------------------------------

        public sealed class Declaration
        {
            public string Property;         // as written
            public Value Value;
            public int Line;
            public string RawValue;         // value text as written (trimmed)
            public override string ToString() { return Property + ": " + RawValue + ";"; }
        }

        // ---- document ---------------------------------------------------------------------

        public sealed class Rule
        {
            public string Sheet;
            public Selector Selector;
            public List<Declaration> Declarations = new List<Declaration>();
            public int Line;
            public int Order;

            public string Get(string property)
            {
                // last declaration wins, like directui's property table.
                for (int i = Declarations.Count - 1; i >= 0; i--)
                    if (string.Equals(Declarations[i].Property, property, StringComparison.OrdinalIgnoreCase)) return Declarations[i].RawValue;
                return null;
            }
            public override string ToString() { return (Selector == null ? "?" : Selector.Raw) + " { " + (Declarations.Count) + " decls }"; }
        }

        public sealed class Sheet
        {
            public string Resid;
            public int Line;
            public List<Rule> Rules = new List<Rule>();
        }

        public sealed class Document
        {
            public List<Sheet> Sheets = new List<Sheet>();
            public List<string> Warnings = new List<string>();
            /// <summary>the legacy DUI element markup that shares the resource with the stylesheets (xp's pane tree),
            /// verbatim. Empty for a resource that only holds sheets.</summary>
            public string MarkupText = "";
            public Sheet Find(string resid)
            {
                for (int i = 0; i < Sheets.Count; i++)
                    if (string.Equals(Sheets[i].Resid, resid, StringComparison.OrdinalIgnoreCase)) return Sheets[i];
                return null;
            }
        }

        /// <summary>Parse an XP shellstyle UIFILE 1 (or 2) text.</summary>
        public static Document Parse(string text)
        {
            var doc = new Document();
            if (text == null) return doc;
            var diag = new Diagnostics();
            string src = StripComments(text, diag);
            int i = 0, line = 1;
            var markup = new StringBuilder();
            while (i < src.Length)
            {
                if (char.IsWhiteSpace(src[i])) { if (src[i] == '\n') line++; i++; continue; }
                if (src[i] == '<')
                {
                    int tagEnd = src.IndexOf('>', i + 1);
                    if (tagEnd < 0) { diag.Add("line " + line + ": unterminated tag"); break; }
                    string tag = src.Substring(i, tagEnd - i + 1);
                    i = tagEnd + 1;
                    if (IsStyleOpen(tag, out string resid))
                    {
                        var sheet = new Sheet { Resid = resid, Line = line };
                        doc.Sheets.Add(sheet);
                        bool closed = ParseStyleBody(src, ref i, ref line, sheet, diag);
                        if (!closed) diag.Add("line " + sheet.Line + ": <style resid=" + resid + "> is not closed");
                        continue;
                    }
                    // Other markup: the legacy DUI element tree that may share this resource. Keep it verbatim.
                    markup.Append(tag);
                }
                // Markup (xp's legacy DUI element tree) lives in the same resource after the stylesheets. It is not
                // part of the stylesheet grammar, but the resource is valid input: skip it without a diagnostic and
                // keep its text so callers can inspect the pane tree (the translator only maps stylesheet rules).
                int markupStart = i;
                SkipTo(src, ref i, ref line, '<');
                if (i > markupStart) markup.Append(src, markupStart, i - markupStart);
            }
            doc.Warnings.AddRange(diag.Messages);
            doc.MarkupText = markup.ToString();
            return doc;
        }

        /// <summary>
        /// Remove <c>/* ... */</c> and <c>// ...</c> comments, leaving blanks in their place so line numbers and
        /// offsets stay exact. xp's scanner treats both as whitespace between any two tokens (scan.l COMMENT /
        /// INLINECOMMENT), and a line comment ends at the newline. Quotes are respected, so a <c>//</c> inside a
        /// string value stays.
        /// </summary>
        private static string StripComments(string text, Diagnostics diag)
        {
            var sb = new StringBuilder(text.Length);
            int line = 1;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != 0)
                {
                    sb.Append(c);
                    if (c == quote) quote = '\0';
                    else if (c == '\n') line++;
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; sb.Append(c); continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) { diag.Add("line " + line + ": unterminated /* comment"); sb.Append(text, i, text.Length - i); break; }
                    for (int k = i; k < end + 2; k++) sb.Append(text[k] == '\n' ? '\n' : ' ');
                    line += CountLines(text, i, end + 2 - i);
                    i = end + 1;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    int end = text.IndexOf('\n', i + 2);
                    if (end < 0) end = text.Length;
                    for (int k = i; k < end; k++) sb.Append(' ');
                    i = end - 1;
                    continue;
                }
                if (c == '\n') line++;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static void SkipTo(string text, ref int i, ref int line, char stop)
        {
            while (i < text.Length && text[i] != stop)
            {
                if (text[i] == '\n') line++;
                i++;
            }
        }

        private static int CountLines(string text, int start, int length)
        {
            int n = 0;
            for (int i = start; i < start + length && i < text.Length; i++) if (text[i] == '\n') n++;
            return n;
        }

        private static bool IsStyleOpen(string tag, out string resid)
        {
            resid = null;
            if (!tag.StartsWith("<", StringComparison.Ordinal) || tag.StartsWith("</", StringComparison.Ordinal)) return false;
            int i = 1; while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
            int start = i; while (i < tag.Length && (char.IsLetterOrDigit(tag[i]) || tag[i] == '_')) i++;
            if (i - start != 5 || !tag.Substring(start, 5).Equals("style", StringComparison.OrdinalIgnoreCase)) return false;
            // resid = name | "name"
            int eq = tag.IndexOf('=', i);
            if (eq < 0) return false;
            i = eq + 1; while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
            if (i < tag.Length && (tag[i] == '"' || tag[i] == '\''))
            {
                char q = tag[i++]; int s = i;
                while (i < tag.Length && tag[i] != q) i++;
                resid = tag.Substring(s, i - s);
            }
            else
            {
                int s = i;
                while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] != '>' && tag[i] != '/') i++;
                resid = tag.Substring(s, i - s);
            }
            return resid.Length != 0;
        }

        private static bool ParseStyleBody(string text, ref int i, ref int line, Sheet sheet, Diagnostics diag)
        {
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { if (text[i] == '\n') line++; i++; continue; }
                if (text[i] == '<' && text.Substring(i).StartsWith("</style", StringComparison.OrdinalIgnoreCase))
                {
                    int end = text.IndexOf('>', i);
                    if (end < 0) return false;
                    i = end + 1; return true;
                }
                if (text[i] == '<')
                {
                    // Nested markup in a style block (some packs keep <style> wrappers): skip it.
                    int end = text.IndexOf('>', i);
                    if (end < 0) return false;
                    line += CountLines(text, i, end - i);
                    i = end + 1; continue;
                }

                int ruleLine = line;
                int selStart = i;
                int brace = FindTopLevelInBody(text, i, '{');
                if (brace < 0) { diag.Add("line " + line + ": selector without a { body"); return false; }
                string selectorText = text.Substring(selStart, brace - selStart);
                line += CountLines(text, selStart, brace - selStart);
                Selector sel = ParseSelector(selectorText, diag, ruleLine);
                i = brace + 1;

                var rule = new Rule { Sheet = sheet.Resid, Selector = sel, Line = ruleLine, Order = sheet.Rules.Count };
                int bodyStart = i;
                // the body starts on the line the '{' is on; FindMatchingBrace advances `line` past the body, so
                // the declarations are numbered from the line captured here, not from a recount of the text.
                int bodyLine = line;
                int close = FindMatchingBrace(text, ref i, ref line);
                if (close < 0) { diag.Add("line " + ruleLine + ": rule body not closed (missing })"); return false; }
                string body = text.Substring(bodyStart, close - bodyStart);
                ParseDeclarations(body, bodyLine, rule, diag);
                i = close + 1;
                // Drop rules whose selector didn't parse: they can't be matched, and keeping them would make the
                // translator silently drop them later anyway.
                if (sel != null) sheet.Rules.Add(rule);
            }
            return false;
        }

        private static int FindTopLevelInBody(string text, int start, char want)
        {
            char quote = '\0'; int paren = 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != 0) { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '(') paren++;
                else if (c == ')') paren = Math.Max(0, paren - 1);
                else if (c == want && paren == 0 && quote == 0) return i;
            }
            return -1;
        }

        private static int FindMatchingBrace(string text, ref int i, ref int line)
        {
            int depth = 1; char quote = '\0';
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != 0)
                {
                    if (c == quote) quote = '\0';
                    if (c == '\n') line++;
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
                else if (c == '\n') line++;
            }
            return -1;
        }

        private static void ParseDeclarations(string body, int line, Rule rule, Diagnostics diag)
        {
            int start = 0; char quote = '\0'; int paren = 0;
            for (int i = 0; i <= body.Length; i++)
            {
                if (i == body.Length) { ParseDeclaration(body.Substring(start), line, rule, diag); break; }
                char c = body[i];
                if (quote != 0)
                {
                    if (c == quote) quote = '\0';
                    if (c == '\n') line++;
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '(') paren++;
                else if (c == ')') paren = Math.Max(0, paren - 1);
                else if (c == ';' && paren == 0)
                {
                    ParseDeclaration(body.Substring(start, i - start), line, rule, diag);
                    start = i + 1;
                }
                else if (c == '\n') line++;
            }
        }

        private static void ParseDeclaration(string text, int line, Rule rule, Diagnostics diag)
        {
            int colon = FindDeclarationColon(text);
            if (colon <= 0)
            {
                if (text.Trim().Length != 0) diag.Add("line " + line + ": declaration without ':' in \"" + text.Trim() + "\"");
                return;
            }
            string prop = text.Substring(0, colon).Trim();
            string valText = text.Substring(colon + 1).Trim().TrimEnd(';').Trim();
            if (prop.Length == 0) { diag.Add("line " + line + ": empty declaration name"); return; }
            rule.Declarations.Add(new Declaration
            {
                Property = prop,
                RawValue = valText,
                Value = ParseValue(valText),
                Line = line,
            });
        }

        private static int FindDeclarationColon(string s)
        {
            // prop : value -- the first top level colon, not one inside a value (url(...), a:b).
            char quote = '\0'; int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != 0) { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == ':' && depth == 0) return i;
            }
            return -1;
        }

    }
}
