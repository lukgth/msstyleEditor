using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using libmsstyle.Import.Dui;

namespace libmsstyle.Import
{
    /// <summary>
    /// One rule of an xp DUI stylesheet: <c>class [attr=value][flag]... { prop: value; }</c>.
    /// </summary>
    public class DuiRule
    {
        /// <summary>the element class the rule is keyed to, lowercase (element, button, tasklist...).</summary>
        public string Class { get; private set; }
        /// <summary>conditions: id atom, or a state flag with its required value (mousefocused, selected, keyfocused).</summary>
        public string Id { get; private set; }

        /// <summary>
        /// every non-id condition of the selector, in source order. xp's sheet.cpp matches any property/value pair
        /// (_CreatePropValPairNode) and counts all of them for specificity (_ComputeSpecif), so conditions this
        /// resolver doesn't model are kept rather than dropping the rule.
        /// </summary>
        public IReadOnlyList<XpUifileSyntax.Condition> Conditions { get; private set; }
        public IReadOnlyDictionary<string, string> Decls { get; private set; }
        public int Order { get; private set; }

        /// <summary>
        /// directui sheet.cpp _ComputeSpecif: (id conditions &lt;&lt; 24) | (all conditions &lt;&lt; 16) | rule order;
        /// the highest matching rule wins (MakeImmutable sorts descending).
        /// </summary>
        public uint Specificity => ((Id != null ? 1u : 0u) << 24) | ((uint)(Conditions.Count + (Id != null ? 1 : 0)) << 16) | (ushort)Order;

        internal static DuiRule From(XpUifileSyntax.Selector selector, Dictionary<string, string> decls, int order)
        {
            if (selector == null) return null;
            string id = null;
            var conditions = selector.StateConditions.ToList();
            var idc = selector.IdCondition;
            if (idc != null)
            {
                // [id=atom(x)] keys the rule to an element id; anything else is kept as its raw text, which no node's
                // atom id equals, so the rule stays in the list (and in the counts) without matching.
                id = (idc.Atom ?? idc.Text ?? "").ToLowerInvariant();
            }
            return new DuiRule { Class = selector.Class.ToLowerInvariant(), Id = id, Conditions = conditions, Decls = decls, Order = order };
        }
    }

    /// <summary>
    /// Resolves what xp's DirectUI actually painted for a task pane element, from the shellstyle's rules, the way the
    /// GDI build shell32 links (directui.lib, no GADGET_ENABLE_GDIPLUS) does it:
    ///
    ///  - a sheet holds rules per element class only (sheet.cpp <c>_pDB[pci->GetGlobalIndex()]</c>): an
    ///    <c>element</c> rule matches plain Elements, not Buttons or TaskLists;
    ///  - among matching rules the highest specificity wins (id conditions, then condition count, then later rule);
    ///  - with no rule, a PF_Inherit property comes from the parent (property.cpp); background, foreground, font
    ///    and bordercolor inherit in that build (element.cpp), padding, margin and borderthickness don't;
    ///  - an element's sheet is its own SheetProp or its parent's (Sheet is PF_Inherit): shell32 sets sectionss /
    ///    mainsectionss on each expando and sectiontaskss / mainsectiontaskss on each task (duiview.cpp), so a task
    ///    looks up the task sheet first and, through inheritance, gets what its section resolved.
    ///
    /// The element tree is shell32's folder.uix (section, mainsection) and duitask.cpp (tasks).
    /// </summary>
    public class DuiCascade
    {
        /// <summary>one element of the tree: DUI class, id atom, state.</summary>
        public class Node
        {
            public string Class;
            public string Id;
            public Node Parent;
            public string Sheet;  // own SheetProp, null = parent's
            public bool MouseFocused, Selected, KeyFocused;
            /// <summary>xp's other state properties a rule can condition on (button.cpp Pressed/Captured, element.cpp
            /// Enabled, scrollbar.cpp Vertical, expando Expanded). Enabled defaults to true like EnabledProp.</summary>
            public bool Pressed, Captured, Enabled = true, Vertical, Expanded;

            /// <summary>the state a rule condition names, or null when this node doesn't have that property.</summary>
            public bool? State(string name)
            {
                switch (name.ToLowerInvariant())
                {
                    case "mousefocused": return MouseFocused;
                    case "selected": return Selected;
                    case "keyfocused": return KeyFocused;
                    case "pressed": return Pressed;
                    case "captured": return Captured;
                    case "enabled": return Enabled;
                    case "vertical": return Vertical;
                    case "expanded": return Expanded;
                    default: return null;
                }
            }
        }

        private static readonly HashSet<string> Inherited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "background", "foreground", "bordercolor", "fontface", "fontsize", "fontweight", "fontstyle", "contentalign", "cursor",
        };

        private readonly XpShellStyle m_style;
        public DuiCascade(XpShellStyle style) { m_style = style; }

        /// <summary>the value text DirectUI uses for <paramref name="property"/> on <paramref name="node"/>; null = the default.</summary>
        public string Resolve(Node node, string property) => Resolve(node, property, out _);

        /// <param name="origin">the element whose rule set the value (the node itself, or the ancestor it inherited from).</param>
        public string Resolve(Node node, string property, out Node origin)
        {
            origin = null;
            for (var n = node; n != null; n = n.Parent)
            {
                string sheet = SheetOf(n);
                if (sheet != null)
                {
                    var hit = m_style.Rules(sheet)
                        .Where(r => Matches(r, n) && r.Decls.ContainsKey(property))
                        .OrderByDescending(r => r.Specificity).FirstOrDefault();
                    if (hit != null) { origin = n; return m_style.MapSysColor(hit.Decls[property]); }
                }
                if (!Inherited.Contains(property)) return null;
            }
            return null;
        }

        private static string SheetOf(Node n)
        {
            for (; n != null; n = n.Parent) if (n.Sheet != null) return n.Sheet;
            return null;
        }

        private static bool Matches(DuiRule r, Node n)
        {
            if (!string.Equals(r.Class, n.Class, StringComparison.OrdinalIgnoreCase)) return false;
            if (r.Id != null && !string.Equals(r.Id, n.Id, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var c in r.Conditions)
            {
                // sheet.cpp GetSheetValue: a condition is a property/value pair, compared for equality (or
                // inequality for PALOGOP_NotEqual). `class` is the one property of the element this tree knows
                // besides its states (scrollbar_win32.uix: `repeatbutton [class="Page"][pressed]`).
                if (string.Equals(c.Name, "class", StringComparison.OrdinalIgnoreCase))
                {
                    bool same = c.Text != null && string.Equals(c.Text.Trim().Trim('"', '\''), n.Class, StringComparison.OrdinalIgnoreCase);
                    if (same == c.Negated) return false;
                    continue;
                }
                // Only the states this tree models can be decided; a condition on anything else is not assumed to
                // hold, so the rule stays out rather than guessing.
                bool? v = n.State(c.Name);
                if (v == null) return false;
                bool want = c.Value == null
                    || (c.Value.Kind == XpUifileSyntax.ValueKind.Ident && c.Value.Text.Equals("true", StringComparison.OrdinalIgnoreCase));
                if (v.Value != (c.Negated ? !want : want)) return false;
            }
            return true;
        }

        // ---- shell32's task pane tree (folder.uix, duitask.cpp) ------------------------------------------

        /// <summary>the parts of one section the win10 ExplorerBar records stand for.</summary>
        public class Section
        {
            public Node Pane, Expando, Wrapper, Header, Title, Arrow, Clipper, Watermark, TaskList, Task, TaskButton, TaskTitle;
        }

        /// <summary>
        /// a section as shell32 builds it, under the pane (folder.uix: hwndview with the "main" sheet, the scroller with
        /// the "taskpane" sheet, the sectionlist). main: folder.uix mainsection (header button in a wrapper element, a
        /// watermark element around the tasklist), else section. <paramref name="open"/>: the expando is selected
        /// (Selected is PF_Inherit, so everything inside reports it). <paramref name="hot"/>: the header button and the
        /// task button are under the mouse; MouseFocused inherits to their children.
        /// </summary>
        public static Section Build(bool main, bool hot = false, bool open = true)
        {
            var s = new Section();
            var view = new Node { Class = "hwndview", Id = "main", Sheet = "main" };
            var scroller = new Node { Class = "scrollviewer", Id = "scroller", Parent = view, Sheet = "taskpane" };
            s.Pane = new Node { Class = "element", Id = "sectionlist", Parent = scroller };
            Node N(string c, string id, Node parent, string sheet = null, bool mf = false)
                => new Node { Class = c, Id = id, Parent = parent, Sheet = sheet, Selected = open, MouseFocused = mf };
            s.Expando = N("expando", null, s.Pane, main ? "mainsectionss" : "sectionss");
            s.Expando.Expanded = open;
            Node top = s.Expando;
            if (main) top = s.Wrapper = N("element", null, s.Expando);
            s.Header = N("button", "header", top, mf: hot);
            s.Title = N("element", "title", s.Header, mf: hot);
            s.Arrow = N("element", "arrow", s.Header, mf: hot);
            s.Clipper = N("clipper", null, s.Expando);
            s.Clipper.Expanded = open;
            Node listParent = s.Clipper;
            if (main) listParent = s.Watermark = N("element", "watermark", s.Clipper);
            s.TaskList = N("tasklist", "tasklist", listParent);
            // the pane's rows are destination tasks ("Other Places" links, duiview.cpp:744 DestinationTask::Create);
            // action tasks (duiview.cpp:707) are the command links of a folder's own task pane, a different element
            // class, and are styled by actiontask rules the shellstyle keeps apart from destinationtask.
            s.Task = N("destinationtask", null, s.TaskList, main ? "mainsectiontaskss" : "sectiontaskss");
            s.TaskButton = N("button", null, s.Task, mf: hot);
            s.TaskTitle = N("element", "title", s.TaskButton, mf: hot);
            return s;
        }
    }
}
