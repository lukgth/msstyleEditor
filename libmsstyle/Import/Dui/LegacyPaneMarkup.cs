using System;
using System.Collections.Generic;
using System.Linq;

namespace libmsstyle.Import.Dui
{
    /// <summary>one element of xp's legacy DirectUI markup (the part after the stylesheets in UIFILE 1).</summary>
    public sealed class LegacyElement
    {
        public LegacyElement(string name, LegacyElement parent)
        {
            Name = name.ToLowerInvariant();
            Parent = parent;
        }

        public string Name { get; }
        public LegacyElement Parent { get; }
        public List<LegacyElement> Children { get; } = new List<LegacyElement>();
        private readonly Dictionary<string, string> m_attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<KeyValuePair<string, string>> Attributes { get { return m_attributes; } }

        public string this[string name]
        {
            get { return m_attributes.TryGetValue(name, out string value) ? value : null; }
        }

        public void Set(string name, string value)
        {
            m_attributes[name] = value;
        }

        public bool Has(string name)
        {
            return m_attributes.ContainsKey(name);
        }

        public IEnumerable<LegacyElement> Descendants()
        {
            foreach (var child in Children)
            {
                yield return child;
                foreach (var descendant in child.Descendants())
                    yield return descendant;
            }
        }

        public LegacyElement FirstDescendant(string name)
        {
            return Descendants().FirstOrDefault(e => e.Name == name.ToLowerInvariant());
        }

        /// <summary>the first descendant with this tag that also has the given attribute (e.g. a scrollviewer with a height).</summary>
        public LegacyElement FirstDescendant(string name, string attribute)
        {
            return Descendants().FirstOrDefault(e => e.Name == name.ToLowerInvariant() && e.Has(attribute));
        }

        public override string ToString()
        {
            return "<" + Name + (Has("resid") ? " resid=" + this["resid"] : "") + ">";
        }
    }

    public enum LegacyPanePlacement
    {
        Unknown,
        Left,
        Top,
        Right,
        Bottom
    }

    /// <summary>
    /// The pane structure xp's shellstyle markup asks for. Xp's shellstyle carries the WebView's element tree
    /// (<c>&lt;element resid=main&gt;</c>, the section list, and the main/section <c>&lt;expando&gt;</c> templates), while
    /// Windows 10's folder layouts are separate resources in the same shellstyle. This reads the parts of xp's tree
    /// that decide where the task pane lives, so the porter can choose the matching win10 layout instead of guessing.
    /// </summary>
    public sealed class LegacyPaneLayout
    {
        public LegacyPanePlacement Placement { get; private set; } = LegacyPanePlacement.Unknown;
        /// <summary>the raw layoutpos of the scrollviewer around the section list ("top", "bottom", "left", ...).</summary>
        public string PlacementText { get; private set; }
        /// <summary>the section list's layout, e.g. gridlayout(0,0,3), verticalflowlayout(0,0,3).</summary>
        public string SectionListLayout { get; private set; }
        /// <summary>height of the pane's scrollviewer in xp (pixels at 96 dpi), without a unit suffix.</summary>
        public int BandHeight { get; private set; }
        /// <summary>width of xp's main section expando (the per-section width of a band).</summary>
        public int ExpandoWidth { get; private set; }
        /// <summary>height declared by the watermark element inside the main expando.</summary>
        public int WatermarkHeight { get; private set; }
        /// <summary>where xp put the header inside its section (left = vertical strip, top = horizontal bar).</summary>
        public string HeaderLayoutPos { get; private set; }
        /// <summary>the element carrying <c>resid=main</c>, if the file has one.</summary>
        public string MainResid { get; private set; }

        public bool IsBand { get { return Placement == LegacyPanePlacement.Top || Placement == LegacyPanePlacement.Bottom; } }

        public static LegacyPaneLayout From(string markup)
        {
            return FromDocuments(new[] { markup });
        }

        /// <summary>
        /// the layout of the first markup that carries a <c>resid=main</c> tree. A shellstyle can hold more than one
        /// UIFILE (xp's style.ui and cpstyle.ui), each with its own element tree, so every document's markup is
        /// offered here rather than only the first one's.
        /// </summary>
        public static LegacyPaneLayout FromDocuments(IEnumerable<string> markups)
        {
            var result = new LegacyPaneLayout();
            var roots = new List<LegacyElement>();
            if (markups != null)
                foreach (var markup in markups)
                {
                    var root = LegacyMarkup.Parse(markup);
                    if (root != null) roots.Add(root);
                }
            if (roots.Count == 0) return result;

            LegacyElement Find(Func<LegacyElement, bool> match)
            {
                foreach (var root in roots)
                {
                    var hit = root.Descendants().FirstOrDefault(match);
                    if (hit != null) return hit;
                }
                return null;
            }

            var main = Find(e => e.Has("resid") && string.Equals(e["resid"], "main", StringComparison.OrdinalIgnoreCase));
            if (main == null) return result;
            result.MainResid = main["resid"];

            // the scrollviewer that actually contains the section list; xp has a second, empty scrollviewer for the
            // pane's scrollbar (height=0) in front of it.
            var sectionList = main.Descendants().FirstOrDefault(e => e.Name == "element" && HasId(e, "sectionlist"));
            if (sectionList != null)
            {
                result.SectionListLayout = sectionList["layout"];
                var scroller = sectionList.Parent;
                while (scroller != null && scroller != main && scroller.Name != "scrollviewer")
                    scroller = scroller.Parent;
                if (scroller != null && scroller != main && scroller.Name == "scrollviewer")
                {
                    result.PlacementText = scroller["layoutpos"];
                    result.Placement = ParsePlacement(result.PlacementText);
                    result.BandHeight = Length(scroller["height"]);
                }
            }

            var mainSection = Find(e => e.Name == "expando" &&
                e.Has("resid") && string.Equals(e["resid"], "mainsection", StringComparison.OrdinalIgnoreCase));
            if (mainSection != null)
            {
                result.ExpandoWidth = Length(mainSection["width"]);
                var header = mainSection.Descendants().FirstOrDefault(e => e.Name == "button" && HasId(e, "header"));
                if (header != null) result.HeaderLayoutPos = header["layoutpos"];
                var watermark = mainSection.Descendants().FirstOrDefault(e => e.Name == "element" && HasId(e, "watermark"));
                if (watermark != null) result.WatermarkHeight = Length(watermark["height"]);
            }
            return result;
        }

        /// <summary>whether the element's id is exactly <c>atom(<paramref name="id"/>)</c>; <c>atom(sectionlist2)</c>
        /// is a different element, so the match is on the whole token, not a substring.</summary>
        private static bool HasId(LegacyElement e, string id)
        {
            string value = e["id"];
            return value != null && string.Equals(value.Trim(), "atom(" + id + ")", StringComparison.OrdinalIgnoreCase);
        }

        private static LegacyPanePlacement ParsePlacement(string text)
        {
            if (string.IsNullOrEmpty(text)) return LegacyPanePlacement.Unknown;
            switch (text.Trim().ToLowerInvariant())
            {
                case "left": return LegacyPanePlacement.Left;
                case "top": return LegacyPanePlacement.Top;
                case "right": return LegacyPanePlacement.Right;
                case "bottom": return LegacyPanePlacement.Bottom;
                default: return LegacyPanePlacement.Unknown;
            }
        }

        /// <summary>
        /// an xp markup length ("54", "300rp") in pixels at 96 dpi, the dpi the porter lays the theme out at.
        /// DirectUI scales a length when it parses it (parse.y <c>number: magnitude YYRP</c> ->
        /// <c>RelPixToPixel</c> = MulDiv(n, dpi, 96)), so rp is a relative pixel and a bare number is one too; any
        /// other suffix is not a length this reads, and junk is rejected rather than truncated to its digits.
        /// </summary>
        private static int Length(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            text = text.Trim();
            int end = 0;
            if (end < text.Length && (text[end] == '-' || text[end] == '+')) end++;
            int digits = end;
            while (end < text.Length && char.IsDigit(text[end])) end++;
            if (end == digits) return 0;
            string unit = text.Substring(end).Trim();
            if (unit.Length != 0 && !unit.Equals("rp", StringComparison.OrdinalIgnoreCase) && !unit.Equals("px", StringComparison.OrdinalIgnoreCase)) return 0;
            return int.TryParse(text.Substring(0, end), out int value) ? value : 0;
        }
    }

    /// <summary>parser for the legacy markup: unquoted attributes, self-closing tags, comments, any tag case.</summary>
    public static class LegacyMarkup
    {
        public static LegacyElement Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var root = new LegacyElement("#document", null);
            var stack = new List<LegacyElement> { root };
            int i = 0;
            while (i < text.Length)
            {
                // xp's markup uses // line comments between elements (folder.uix:13-18 comments out two elements
                // that way); scan for the next tag, dropping them, so they don't become elements of the tree.
                int open = -1;
                while (i < text.Length)
                {
                    if (Starts(text, i, "//"))
                    {
                        int eol = text.IndexOf('\n', i + 2);
                        i = eol < 0 ? text.Length : eol + 1;
                        continue;
                    }
                    if (text[i] == '<') { open = i; break; }
                    i++;
                }
                if (open < 0) break;
                if (Starts(text, open, "<!--"))
                {
                    int end = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 3;
                    continue;
                }
                if (Starts(text, open, "<!"))
                {
                    int end = text.IndexOf('>', open + 2);
                    i = end < 0 ? text.Length : end + 1;
                    continue;
                }
                if (Starts(text, open, "<?"))
                {
                    int end = text.IndexOf("?>", open + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                    continue;
                }
                bool closing = open + 1 < text.Length && text[open + 1] == '/';
                int at = open + (closing ? 2 : 1);
                int nameStart = at;
                while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] != '>' && text[at] != '/') at++;
                if (at == nameStart) { i = open + 1; continue; }
                string name = text.Substring(nameStart, at - nameStart);
                if (closing)
                {
                    int end = text.IndexOf('>', at);
                    i = end < 0 ? text.Length : end + 1;
                    // pop to the matching open element; tolerate stray closing tags
                    int match = stack.FindLastIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (match > 0) stack.RemoveRange(match, stack.Count - match);
                    continue;
                }

                var element = new LegacyElement(name, stack[stack.Count - 1]);
                stack[stack.Count - 1].Children.Add(element);
                bool selfClosing = false;
                while (at < text.Length)
                {
                    while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
                    if (at >= text.Length) break;
                    if (text[at] == '>') { at++; break; }
                    if (text[at] == '/')
                    {
                        if (at + 1 < text.Length && text[at + 1] == '>') { selfClosing = true; at += 2; }
                        else at++;
                        break;
                    }
                    int attrStart = at;
                    while (at < text.Length && text[at] != '=' && !char.IsWhiteSpace(text[at]) && text[at] != '>' && text[at] != '/') at++;
                    string attrName = text.Substring(attrStart, at - attrStart);
                    while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
                    if (at >= text.Length || text[at] != '=')
                    {
                        if (attrName.Length > 0) element.Set(attrName, string.Empty);
                        continue;
                    }
                    at++;
                    while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
                    string value;
                    if (at < text.Length && (text[at] == '"' || text[at] == '\''))
                    {
                        char quote = text[at++];
                        int valueStart = at;
                        while (at < text.Length && text[at] != quote) at++;
                        value = text.Substring(valueStart, at - valueStart);
                        if (at < text.Length) at++;
                    }
                    else
                    {
                        // an unquoted value ends at whitespace, '>' or '/', and at the closing paren of a call:
                        // folder.uix writes `id=atom(listview)layoutpos=client` with the next attribute glued on,
                        // so the value must end where the atom() call does.
                        int valueStart = at;
                        int depth = 0;
                        while (at < text.Length)
                        {
                            char c = text[at];
                            if (c == '(') depth++;
                            else if (c == ')')
                            {
                                at++;
                                if (depth > 0 && --depth == 0) break;
                                continue;
                            }
                            else if (depth == 0 && (char.IsWhiteSpace(c) || c == '>' || c == '/')) break;
                            at++;
                        }
                        value = text.Substring(valueStart, at - valueStart);
                    }
                    if (attrName.Length > 0) element.Set(attrName, value);
                }
                if (!selfClosing) stack.Add(element);
                i = at;
            }
            return root;
        }

        private static bool Starts(string text, int at, string value)
        {
            return at + value.Length <= text.Length && string.CompareOrdinal(text, at, value, 0, value.Length) == 0;
        }
    }
}
