using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace libmsstyle.Import.Dui
{
    /// <summary>Resources the translator needs from the source shellstyle and the theme it belongs to.</summary>
    public interface IXpShellResources
    {
        /// <summary>String table entry used by rcstr(id); null when missing.</summary>
        string ResourceString(int id);
        /// <summary>Integer string table entry used by rcint(id); null when missing or not numeric.</summary>
        int? ResourceInt(int id);
        /// <summary>sysmetricstr(index) value (a font face, for index 6); null when unknown.</summary>
        string SystemMetricString(int index);
        /// <summary>theme class name for dtb(handlemap(index), ...); the xp theme's [Handlemaps] table. Null when unknown.</summary>
        string Handlemap(int index);
        /// <summary>whether the source shellstyle carries RT_BITMAP <paramref name="id"/>; rcbmp(id) with a
        /// missing bitmap is a dangling xp reference that win10's DUI parser rejects outright.</summary>
        bool HasBitmap(int id);
    }

    public sealed class TranslationReport
    {
        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Unsupported = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int RulesSeen, RulesChanged, RulesIdentical, RulesSpecialized, RulesTranslated, PropertiesTranslated;
        public override string ToString()
        {
            return $"{RulesSeen} rules seen, {RulesIdentical} identical to stock, {RulesChanged} differ from stock, " +
                   $"{RulesSpecialized} covered by specialized imports, {RulesTranslated} translated, {PropertiesTranslated} declarations mapped, {Unsupported.Count} unsupported";
        }
    }

    /// <summary>
    /// XP UIFILE (legacy DUI CSS) to Win10 DUIXML translation.
    ///
    /// The target shellstyle is not a blank sheet: its ExplorerBarStyle sheet drives Win10's folder pane tree
    /// through the msstyles records the porter writes. This translator therefore maps the source's *changed*
    /// rules (diffed against the stock XP sheet) onto the template's existing class rules:
    ///
    ///   xp selector                          win10 condition class / tag
    ///   element [id=atom(sectionlist)]       ExplorerBarBackground / element
    ///   expando                              global Expando (margin -> padding)
    ///   button, button [id=atom(header)]     SpecialGroupHeader, NormalGroupHeader / accessiblebutton
    ///   element [id=atom(title)]             name_special, name_normal / button
    ///   element [id=atom(arrow)]             SpecialGroupExpand, NormalGroupExpand / Button
    ///   clipper (selected)                   SpecialGroupBackground, NormalGroupBackground / clipper
    ///   tasklist [id=atom(tasklist)]         id=atom(tasklist) / element
    ///   button (task sheets)                 label_special, label_normal / Button
    ///   destinationtask, actiontask          task_item / element
    ///
    /// Values are translated structurally: rcbmp -> graphic (the porter copies xp's RT_BITMAPs with their ids),
    /// rcstr/rcint/sysmetricstr are resolved to literals at import time (the target's string table is the
    /// template's, not the source's), dtb(handlemap(n), ...) is resolved through the theme's handlemap table,
    /// colors, gradients, rects/points, sysmetrics and units are shared syntax and stay. Font declarations
    /// (fontface/fontsize/fontweight) use resolved gtf() records imported separately; fontstyle has a
    /// verified DUIXML attribute. Literal font strings are never guessed.
    /// </summary>
    public static class XpShellTranslator
    {
        private sealed class Target
        {
            public string Style;             // template sheet (ExplorerBarStyle / TaskButtonStyle)
            public string Class;             // class condition, null for a global element
            public string GlobalTag;         // global element tag instead of a class block
            public string Tag;               // element tag inside the block
            public bool MarginAsPadding;     // xp expando/task margins are padding in the win10 tree
            public bool Auxiliary;           // not owned by the pane bitmap/inset/layout passes
        }

        /// <summary>Translate the source rules into the template tree, modifying <paramref name="template"/>.
        /// <paramref name="stock"/> is the same pack's stock sheet: only declarations that differ from stock are
        /// applied, so a stock source leaves the template untouched.</summary>
        public static TranslationReport Translate(XpUifileSyntax.Document source, XpUifileSyntax.Document stock,
            DuiXmlDocument template, IXpShellResources resources, ISet<string> properties = null,
            bool reportMissingTargets = true, IReadOnlyDictionary<string, string> auxiliaryFonts = null)
        {
            var report = new TranslationReport();
            if (source == null) { report.Warnings.Add("no source stylesheet to translate"); return report; }
            if (template == null) { report.Warnings.Add("no template UIFILE to translate into"); return report; }
            // the parser's diagnostics are the only report of malformed source rules; hand them on so the import
            // report carries them instead of the parser's findings dying with the parse.
            foreach (var warning in source.Warnings) report.Warnings.Add("shellstyle source: " + warning);
            var explorerBar = template.FindStyle("ExplorerBarStyle") ?? template.FindStyle("VistaExplorerBarStyle");
            var taskButton = template.FindStyle("TaskButtonStyle");

            foreach (var sheet in source.Sheets)
            {
                foreach (var rule in sheet.Rules)
                {
                    report.RulesSeen++;
                    var stockRule = stock == null ? null : SameRuleText(stock, sheet.Resid, rule.Selector);
                    // "changed" is about the source rule, not about the subset this import translates: a rule that
                    // differs from stock only in a property outside `properties` still differs, and saying otherwise
                    // makes the counts read as if the source had fewer rules than it has.
                    bool changed = false;
                    foreach (var decl in rule.Declarations)
                        if (stockRule == null || !SameDeclaration(stockRule, decl)) { changed = true; break; }
                    if (!changed) { report.RulesIdentical++; continue; }
                    report.RulesChanged++;
                    var targets = TargetsFor(sheet.Resid, rule.Selector, explorerBar, taskButton);
                    AddAuxiliaryTargets(template, sheet.Resid, rule.Selector, targets);
                    bool specialized = false;
                    if (targets.Count == 0)
                    {
                        if (SpecializedWithoutTarget(sheet.Resid, rule))
                        {
                            report.RulesSpecialized++;
                            report.Notes.Add($"{sheet.Resid}: {rule.Selector.Raw}: covered by relocated pane watermark/background records");
                            continue;
                        }
                        if (reportMissingTargets)
                            report.Unsupported.Add($"{sheet.Resid}: {rule.Selector.Raw} -> no existing Win10 stylesheet element; XP structural layout is not synthesized");
                        continue;
                    }
                    var translated = new List<(string attr, string value)>();
                    bool hot = rule.Selector.StateConditions.Any(c => c.Name.Equals("mousefocused", StringComparison.OrdinalIgnoreCase) && !c.Negated);
                    string font = null;
                    auxiliaryFonts?.TryGetValue(FontKey(sheet.Resid, rule.Selector.Class, rule.Selector.AtomId, hot), out font);
                    if (font != null) translated.Add(("font", font));
                    foreach (var decl in rule.Declarations)
                    {
                        if (stockRule != null && SameDeclaration(stockRule, decl)) continue;
                        string property = decl.Property.ToLowerInvariant();
                        if (property == "fontstyle" && IsIconFontMetric(sheet.Resid, rule.Selector, property, decl.RawValue))
                        {
                            specialized = true;
                            report.Notes.Add($"{sheet.Resid}: {rule.Selector.Raw}: icon font style retained through Win10 IconFont");
                            continue;
                        }
                        if (property == "foreground" && properties != null && !properties.Contains(property)
                            && sheet.Resid.Equals("NameSpaceItemInfoList", StringComparison.OrdinalIgnoreCase))
                        {
                            specialized = true;
                            report.Notes.Add($"{sheet.Resid}: {rule.Selector.Raw}: foreground covered by resolved PreviewPane 5/6/7 TEXTCOLOR records");
                            continue;
                        }
                        if (IsFontProperty(property) && property != "fontstyle")
                        {
                            if (font != null || IsPaneSheet(sheet.Resid) || IsIconFontMetric(sheet.Resid, rule.Selector, property, decl.RawValue))
                            {
                                specialized = true;
                                report.Notes.Add($"{sheet.Resid}: {rule.Selector.Raw}: {property} covered by resolved FONT/gtf records");
                            }
                            else report.Unsupported.Add($"{sheet.Resid}: {rule.Selector.Raw}: {property} has no resolved target FONT record");
                            continue;
                        }
                        if (properties != null && !properties.Contains(property)
                            && !(targets.Any(t => t.Auxiliary) && AuxiliaryProperty(property)) && property != "fontstyle")
                        {
                            if (SpecializedProperty(sheet.Resid, rule.Selector, property))
                            {
                                specialized = true;
                                report.Notes.Add($"{sheet.Resid}: {rule.Selector.Raw}: {property} covered by ExplorerBar bitmap/inset/font/layout records");
                            }
                            else report.Unsupported.Add($"{sheet.Resid}: {rule.Selector.Raw}: {property} has no geometry-preserving Win10 mapping");
                            continue;
                        }
                        string attr = AttributeName(decl, rule, targets);
                        if (attr == null) { report.Unsupported.Add($"{sheet.Resid}: {rule.Selector.Raw}: {decl.Property} has no DUIXML equivalent"); continue; }
                        string value = TranslateValue(decl.RawValue, resources, report, sheet.Resid + ": " + rule.Selector.Raw + ": " + decl.Property);
                        if (value == null) continue;
                        if (attr == "animation") value = NormalizeAnimation(value);
                        translated.Add((attr, value));
                    }
                    if (specialized) report.RulesSpecialized++;
                    if (translated.Count == 0) continue;
                    bool applied = false;
                    foreach (var target in targets)
                    {
                        if (Apply(template, target, rule, translated, report)) applied = true;
                    }
                    if (applied) report.RulesTranslated++;
                    else report.Unsupported.Add($"{sheet.Resid}: {rule.Selector.Raw} -> target block missing");
                }
            }
            // The consumer reads this report's Warnings and counts; nothing reads Unsupported, so the per-item
            // reasons are handed over as warnings too, and the summary line makes the counts visible in the import
            // report. Without this every "no DUIXML equivalent" / "unresolvable value" reason dies with the report.
            if (report.Unsupported.Count != 0)
            {
                report.Warnings.Add(report.ToString());
                foreach (var unsupported in report.Unsupported) report.Warnings.Add(unsupported);
            }
            return report;
        }

        /// <summary>
        /// DUIXML writes an animation as <c>token|token|...</c> without spaces; xp's scanner accepts spaces around
        /// the bars, so the tokens are trimmed and rejoined. The names themselves are not rewritten: xp's enum has
        /// both <c>rectangle</c> (ANI_Rect) and <c>rectanglev</c> (ANI_RectV) and they animate different areas.
        /// </summary>
        private static string NormalizeAnimation(string value)
        {
            var parts = value.Split('|');
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return string.Join("|", parts);
        }

        private static bool IsFontProperty(string p)
        {
            switch (p.ToLowerInvariant())
            {
                case "fontface": case "fontsize": case "fontweight": case "fontstyle": case "font": return true;
                default: return false;
            }
        }

        public static string FontKey(string sheet, string element, string atom, bool hot)
            => (sheet ?? "") + "|" + (element ?? "") + "|" + (atom ?? "") + "|" + (hot ? "hot" : "normal");

        private static bool IsPaneSheet(string sheet)
        {
            string s = (sheet ?? "").ToLowerInvariant();
            return s == "sectionss" || s == "mainsectionss" || s == "sectiontaskss" || s == "mainsectiontaskss";
        }

        private static bool SpecializedProperty(string sheet, XpUifileSyntax.Selector selector, string property)
        {
            if (!(IsPaneSheet(sheet) || string.Equals(sheet, "taskpane", StringComparison.OrdinalIgnoreCase))) return false;
            switch (property)
            {
                case "background": case "foreground": case "bordercolor": case "borderthickness":
                case "padding": case "margin": case "contentalign": return true;
                case "content": return selector.AtomId == "arrow" || selector.AtomId == "sectionlist";
                default: return false;
            }
        }

        private static bool SpecializedWithoutTarget(string sheet, XpUifileSyntax.Rule rule)
        {
            if (!IsPaneSheet(sheet) || !string.Equals(rule.Selector.Class, "element", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(rule.Selector.AtomId, "watermark", StringComparison.OrdinalIgnoreCase)) return false;
            return rule.Declarations.All(d => d.Property.Equals("background", StringComparison.OrdinalIgnoreCase)
                || d.Property.Equals("contentalign", StringComparison.OrdinalIgnoreCase)
                || d.Property.Equals("animation", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsIconFontMetric(string sheet, XpUifileSyntax.Selector selector, string property, string value)
        {
            if (!string.Equals(sheet, "main", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(selector.Class, "DUIListView", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(selector.AtomId, "listview", StringComparison.OrdinalIgnoreCase)) return false;
            string v = new string((value ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
            return property == "fontface" && v == "sysmetricstr(6)"
                || property == "fontsize" && v == "sysmetric(-16)"
                || property == "fontweight" && v == "sysmetric(-17)"
                || property == "fontstyle" && v == "sysmetric(-18)";
        }


        private static string AttributeName(XpUifileSyntax.Declaration decl, XpUifileSyntax.Rule rule, List<Target> targets)
        {
            string p = decl.Property.ToLowerInvariant();
            bool marginAsPadding = targets.Any(t => t.MarginAsPadding);
            switch (p)
            {
                case "background": case "foreground": case "content": case "contentalign": case "cursor":
                case "fontstyle":
                case "bordercolor": case "borderthickness": case "alpha": case "animation": case "width": case "height":
                    return p;
                case "padding": return "padding";
                case "margin": return marginAsPadding ? "padding" : null; // win10's tree uses padding where xp spaced with margins
                case "layoutpos": case "layout": return null;
                default: return null;
            }
        }

        /// <summary>Translate one value. Returns null when the value cannot be represented (and records why).</summary>
        public static string TranslateValue(string raw, IXpShellResources resources, TranslationReport report, string where)
        {
            var v = XpUifileSyntax.ParseValue(raw);
            if (v == null) return null;
            return TranslateValueNode(v, resources, report, where);
        }

        private static string TranslateValueNode(XpUifileSyntax.Value v, IXpShellResources resources, TranslationReport report, string where)
        {
            if (v == null) return null;
            switch (v.Kind)
            {
                case XpUifileSyntax.ValueKind.Function:
                    string fn = v.Name.ToLowerInvariant();
                    if (fn == "rcbmp")
                    {
                        // Same argument list, target function. The porter copies the xp shellstyle's RT_BITMAP
                        // resources into the win10 dll preserving their ids, so graphic(n,...) reaches the art.
                        // The transparent-colour key is a #RRGGBB literal in xp, which its scanner reads as a plain
                        // integer (scan.l: '#' -> YYINT); DUIXML's graphic() takes that integer, so it is written
                        // in decimal instead of the hex spelling.
                        // xp tolerated a dangling rcbmp: its scanner resolved the id at draw time and drew nothing.
                        // win10's DUIXMLParserFailure (parse state 9, "Unable to load graphics") aborts the whole
                        // shellstyle instead, so an id the xp dll does not carry must become argb(0,0,0,0).
                        int bitmapId = IntArg(v, 0);
                        if (resources != null && !resources.HasBitmap(bitmapId))
                        {
                            report.Unsupported.Add(where + ": rcbmp(" + bitmapId + ") has no bitmap in the xp shellstyle; xp drew nothing for it, win10's parser rejects the sheet");
                            return "argb(0,0,0,0)";
                        }
                        var sb = new StringBuilder("graphic(");
                        for (int i = 0; i < v.Args.Count; i++)
                        {
                            if (i != 0) sb.Append(", ");
                            var arg = v.Args[i];
                            if (arg.Kind == XpUifileSyntax.ValueKind.Hex &&
                                int.TryParse(arg.Text.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int key))
                                sb.Append(key.ToString(CultureInfo.InvariantCulture));
                            else
                                sb.Append(TranslateValueNode(arg, resources, report, where));
                        }
                        return sb.Append(')').ToString();
                    }
                    if (fn == "rcstr")
                    {
                        int id = IntArg(v, 0);
                        string s = resources == null ? null : resources.ResourceString(id);
                        if (s == null) { report.Unsupported.Add(where + ": rcstr(" + id + ") is not in the xp shellstyle's string table"); return null; }
                        return "'" + s.Replace("'", "''") + "'";
                    }
                    if (fn == "rcint")
                    {
                        int id = IntArg(v, 0);
                        int? n = resources == null ? null : resources.ResourceInt(id);
                        if (n == null) { report.Unsupported.Add(where + ": rcint(" + id + ") is not a numeric string table entry"); return null; }
                        return n.Value.ToString(CultureInfo.InvariantCulture);
                    }
                    if (fn == "sysmetricstr")
                    {
                        int id = IntArg(v, 0);
                        string s = resources == null ? null : resources.SystemMetricString(id);
                        if (s == null) { report.Unsupported.Add(where + ": sysmetricstr(" + id + ") is unknown"); return null; }
                        return "'" + s.Replace("'", "''") + "'";
                    }
                    if (fn == "dtb")
                    {
                        // dtb(handlemap(n), part, state) -> dtb(themeClass, part, state). DirectUI's dtb takes the
                        // theme class by name; the source names it through the theme's handlemap table.
                        var args = new List<string>();
                        if (v.Args.Count == 3 && v.Args[0].IsFunction("handlemap"))
                        {
                            int hm = IntArg(v.Args[0], 0);
                            string cls = resources == null ? null : resources.Handlemap(hm);
                            if (cls == null) { report.Unsupported.Add(where + ": handlemap(" + hm + ") is not in the theme's handlemap table"); return null; }
                            args.Add(cls);
                        }
                        else
                        {
                            report.Unsupported.Add(where + ": dtb() without handlemap() is a class name xp's theme may not define");
                            return null;
                        }
                        for (int i = 1; i < v.Args.Count; i++) args.Add(TranslateValueNode(v.Args[i], resources, report, where));
                        return "dtb(" + string.Join(", ", args) + ")";
                    }
                    if (fn == "sysmetric" || fn == "rgb" || fn == "argb" || fn == "gradient" || fn == "rect" || fn == "point" || fn == "size")
                    {
                        // Win10 DUIXML accepts the same functions and units.
                        var sb = new StringBuilder(v.Name).Append('(');
                        for (int i = 0; i < v.Args.Count; i++)
                        {
                            if (i != 0) sb.Append(", ");
                            sb.Append(TranslateValueNode(v.Args[i], resources, report, where));
                        }
                        return sb.Append(')').ToString();
                    }
                    report.Unsupported.Add(where + ": function " + v.Name + "() is not translated");
                    return null;
                case XpUifileSyntax.ValueKind.String:
                    return "'" + v.Text.Replace("'", "''") + "'";
                case XpUifileSyntax.ValueKind.Number:
                case XpUifileSyntax.ValueKind.Hex:
                    return v.Text;
                case XpUifileSyntax.ValueKind.Ident:
                    // system color names (window, windowtext, highlighttext, ...) and keywords (hand, white) both
                    // have the same spelling in DUIXML.
                    return v.Text;
                case XpUifileSyntax.ValueKind.Binary:
                    return TranslateValueNode(v.Left, resources, report, where) + " | " + TranslateValueNode(v.Right, resources, report, where);
                default:
                    return v.Raw;
            }
        }

        private static int IntArg(XpUifileSyntax.Value fn, int i)
        {
            if (fn.Args.Count <= i) return -1;
            int n;
            return int.TryParse(fn.Args[i].Text ?? fn.Args[i].Raw, out n) ? n : -1;
        }

        // ---- rule / target mapping -----------------------------------------------------

        private static List<Target> TargetsFor(string sheet, XpUifileSyntax.Selector sel, XElement explorerBar, XElement taskButton)
        {
            var r = new List<Target>();
            if (sel == null) return r;
            string cls = sel.Class.ToLowerInvariant();
            string atom = sel.AtomId == null ? null : sel.AtomId.ToLowerInvariant();
            string sh = sheet == null ? "" : sheet.ToLowerInvariant();

            void Add(string style, string className, string tag, bool marginAsPadding = false)
            {
                if ((style == "ExplorerBarStyle" ? explorerBar : taskButton) == null) return;
                r.Add(new Target { Style = style, Class = className, Tag = tag, MarginAsPadding = marginAsPadding });
            }

            if (sh == "taskpane")
            {
                if (cls == "element" && atom == "sectionlist") Add("ExplorerBarStyle", "ExplorerBarBackground", "element");
                return r;
            }
            if (sh.Contains("sectiontasks"))
            {
                // xp loads mainsectiontaskss for the main section's tasks and sectiontaskss for the rest
                // (duiview.cpp:939/:954), and the win10 template keeps the same two groups apart.
                bool mainTasks = sh.Contains("mainsectiontasks");
                if (cls == "button")
                {
                    Add("ExplorerBarStyle", mainTasks ? "label_special" : "label_normal", "Button");
                }
                else if (cls == "destinationtask" || cls == "actiontask")
                    Add("ExplorerBarStyle", "task_item", "element", true);
                else if (cls == "element" && (atom == null || atom == "title"))
                    Add("ExplorerBarStyle", mainTasks ? "label_special" : "label_normal", "Button");
                return r;
            }
            if (!sh.Contains("sections") && !sh.Contains("mainsections")) return r;

            // sectionss styles the normal sections, mainsectionss the main one (duiview.cpp:1113); the template's
            // Special* blocks are the main group and its Normal* blocks the others, so a pack that styles them
            // differently keeps both values instead of one overwriting the other.
            bool main = sh.Contains("mainsections");
            string headerClass = main ? "SpecialGroupHeader" : "NormalGroupHeader";
            string nameClass = main ? "name_special" : "name_normal";
            string expandClass = main ? "SpecialGroupExpand" : "NormalGroupExpand";
            string clipperClass = main ? "SpecialGroupBackground" : "NormalGroupBackground";

            switch (cls)
            {
                case "expando":
                    if (explorerBar != null && DuiXmlDocument.Child(explorerBar, "Expando") != null)
                        r.Add(new Target { Style = "ExplorerBarStyle", GlobalTag = "Expando", MarginAsPadding = true });
                    break;
                case "button":
                    // a sheet matches per class (sheet.cpp:352), so a class-only button rule hits every button in
                    // that sheet: the header, the title button and the expand button. An id condition narrows it.
                    if (atom == null)
                    {
                        Add("ExplorerBarStyle", headerClass, "accessiblebutton");
                        Add("ExplorerBarStyle", nameClass, "button");
                        Add("ExplorerBarStyle", expandClass, "Button");
                    }
                    else if (atom == "header")
                    {
                        Add("ExplorerBarStyle", headerClass, "accessiblebutton");
                    }
                    break;
                case "element":
                    if (atom == "title" || atom == null)
                    {
                        Add("ExplorerBarStyle", nameClass, "button");
                    }
                    else if (atom == "arrow")
                    {
                        Add("ExplorerBarStyle", expandClass, "Button");
                    }
                    if (atom == null) Add("ExplorerBarStyle", expandClass, "Button");
                    break;
                case "clipper":
                    Add("ExplorerBarStyle", clipperClass, "clipper");
                    break;
                case "tasklist":
                    if (atom == "tasklist")
                    {
                        // The template keys this element by id, not class.
                        if (explorerBar != null) r.Add(new Target { Style = "ExplorerBarStyle", Class = "id:tasklist", Tag = "element" });
                    }
                    break;
            }
            return r;
        }

        private static bool AuxiliaryProperty(string property)
        {
            switch (property)
            {
                case "background": case "foreground": case "contentalign": case "bordercolor":
                case "borderthickness": case "fontstyle": case "alpha": case "cursor": case "animation":
                    return true;
                default: return false;
            }
        }

        // Only existing stylesheet elements are reachable. In particular, XP's private scrollbar tree and
        // blockade accent/container elements must not be invented inside an unrelated Win10 layout.
        private static void AddAuxiliaryTargets(DuiXmlDocument doc, string sheet, XpUifileSyntax.Selector sel, List<Target> targets)
        {
            if (sel == null) return;
            string sh = (sheet ?? "").ToLowerInvariant(), cls = sel.Class.ToLowerInvariant();
            string atom = sel.AtomId?.ToLowerInvariant();
            void Add(string styleName, string condition, string tag)
            {
                var style = doc.FindStyle(styleName);
                if (style == null) return;
                var block = condition == null ? style : condition.StartsWith("id:", StringComparison.Ordinal)
                    ? doc.FindIdBlock(style, condition.Substring(3)) : doc.FindClassBlock(style, condition);
                if (DuiXmlDocument.Child(block, tag) == null) return;
                targets.Add(new Target { Style = styleName, Class = condition, Tag = tag,
                    GlobalTag = condition == null ? tag : null, Auxiliary = true });
            }
            if (sh == "main" && cls == "duilistview" && atom == "listview")
                Add("main", "id:listview", "DUIListView");
            if (sh == "main" && cls == "element" && atom == null) Add("main", null, "Element");
            if (sh == "namespaceiteminfolist" && cls == "namespaceiteminfo")
            {
                foreach (var style in doc.Styles)
                {
                    string name = (string)style.Attribute("resid");
                    if (atom == "infoname") Add(name, "id:PreviewMetadataTitleControl", "MetadataElement");
                    if (atom == "infotype") Add(name, "id:PreviewMetadataSubTitleControl", "MetadataElement");
                    if (atom == null) Add(name, "id:PreviewMetadataControl", "MetadataElement");
                }
            }
            if ((sh == "categorylistitemss" || sh == "categorytasklistitemss")
                && (cls == "linkelement" || (cls == "element" && atom == "title")))
            {
                Add("CategoryPanelStyle", sh == "categorylistitemss" ? "id:name" : "id:tasklink", "ControlPanelLink");
                if (sh == "categorylistitemss") Add("CategoryPanelStyle", "custom_links", "element");
            }
            if (sh == "directivess" && cls == "element" && atom == null)
            {
                Add("CategoryPanelStyle", "HomePageTitle", "element");
                Add("CategoryPanelStyle", "id:HomePageTitle", "element");
            }
            if ((sh == "categoryviewss" || sh == "categorylistss") && cls == "element" && atom == "view")
            {
                Add("CategoryPanelStyle", "MainContentPanel", "element");
                // MainContentBackground also draws the relocated watermark: its dtb must stay intact.
            }
            if (sh == "bannerss" && cls == "element" && (atom == "header" || atom == "title"))
                Add("CategoryPanelStyle", "HomePageHeader", "element");
            string barrier = sh == "main" && cls == "element"
                ? atom == "blockade" ? "bp_pane" : atom == "blockadetitle" ? "bp_instruction"
                    : atom == "blockademessage" ? "bp_text" : atom == "blockadecleartext" ? "bp_content_link" : null
                : sh == "barricadess" && cls == "element"
                    ? atom == "barricadetitle" ? "bp_instruction" : atom == "barricademsg" ? "bp_text" : null : null;
            if (barrier != null) Add("barrierpagestyle", barrier, "Element");
            if (barrier == "bp_content_link") Add("barrierpagestyle", barrier, "Button");
            if (sh == "taskpane" || sh == "scrollerss")
            {
                // A template can expose the legacy controls in its own/common sheet; never create controls when
                // Win10 uses the native ScrollBar class instead (its art is covered by the msstyles merge).
                foreach (string targetSheet in new[] { sheet, "common", "ExplorerBarStyle" })
                    if (atom == null) Add(targetSheet, null, sel.Class);
                    else Add(targetSheet, "id:" + atom, sel.Class);
            }
        }

        private static bool Apply(DuiXmlDocument doc, Target target, XpUifileSyntax.Rule rule,
            List<(string attr, string value)> attrs, TranslationReport report)
        {
            if (attrs.Count == 0) return false;
            XElement style = doc.FindStyle(target.Style);
            if (style == null) return false;
            XElement block = null, host;
            if (target.GlobalTag != null)
            {
                host = DuiXmlDocument.Child(style, target.GlobalTag);
                if (host == null) return false;
            }
            else
            {
                if (target.Class != null && target.Class.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                    block = doc.FindIdBlock(style, target.Class.Substring(3));
                else
                    block = doc.FindClassBlock(style, target.Class);
                if (block == null) return false;
                host = DuiXmlDocument.EnsureElement(block, target.Tag);
            }

            var states = new List<XpUifileSyntax.Condition>();
            if (rule.Selector != null)
                foreach (var c in rule.Selector.StateConditions) states.Add(c);

            XElement targetEl = host;
            if (states.Count != 0)
            {
                if (target.GlobalTag != null)
                {
                    report.Unsupported.Add(rule.Selector.Raw + ": a state on the global " + target.GlobalTag + " has no block to nest in");
                    return false;
                }
                XElement cursor = block;
                foreach (var s in states)
                {
                    string name = MapStateName(s.Name, target);
                    if (name == null)
                    {
                        report.Unsupported.Add(rule.Selector.Raw + ": state [" + s.Name + "] has no DUIXML condition");
                        return false;
                    }
                    string value = s.Negated ? "false" : "true";
                    XElement next = null;
                    foreach (var e in cursor.Elements("if"))
                        if (DuiXmlDocument.AttributeIs(e, name, value)) { next = e; break; }
                    if (next == null)
                    {
                        next = new XElement("if");
                        next.SetAttributeValue(name, value);
                        cursor.Add(next);
                    }
                    cursor = next;
                }
                targetEl = DuiXmlDocument.EnsureElement(cursor, target.Tag);
            }

            foreach (var a in attrs)
            {
                // We only translate declarations that differ from the stock sheet, so overriding the template's
                // themeable() record (gtc/gtf/dtb) is intended for those, not a silent replacement of the rest.
                DuiXmlDocument.SetAttribute(targetEl, a.attr, a.value);
                report.PropertiesTranslated++;
            }
            return true;
        }

        /// <summary>
        /// xp state flag -> DUIXML condition attribute. Only names Win10's own sheets use are emitted
        /// (keyfocused, mousefocused, selected, pressed, enabled, Expanded, EvaluatingAsyncState, class); xp's
        /// Captured/MouseWithin/Focused have no verified spelling there, so they are reported instead of guessed.
        /// A clipper's open state is its section's Selected in xp but <c>Expanded</c> in the win10 tree
        /// (allport.uif: <c>&lt;clipper id="atom(clipper)"&gt;</c> with <c>&lt;if Expanded=...&gt;</c>).
        /// </summary>
        private static string MapStateName(string name, Target target)
        {
            string n = name == null ? "" : name.ToLowerInvariant();
            if (target != null && target.Tag == "clipper" && n == "selected") return "Expanded";
            switch (n)
            {
                case "mousefocused": return "MouseFocused";
                case "selected": return "selected";
                case "keyfocused": return "KeyFocused";
                case "pressed": return "pressed";
                case "enabled": return "enabled";
                case "expanded": return "Expanded";
                default: return null;
            }
        }

        private static XpUifileSyntax.Rule SameRuleText(XpUifileSyntax.Document stock, string sheet, XpUifileSyntax.Selector sel)
        {
            var s = stock.Find(sheet);
            if (s == null) return null;
            string raw = XpUifileSyntax.NormalizeSelector(sel.Raw);
            XpUifileSyntax.Rule merged = null;
            foreach (var r in s.Rules)
                if (string.Equals(XpUifileSyntax.NormalizeSelector(r.Selector.Raw), raw, StringComparison.OrdinalIgnoreCase))
                {
                    if (merged == null) merged = new XpUifileSyntax.Rule { Sheet = sheet, Selector = r.Selector };
                    merged.Declarations.AddRange(r.Declarations);
                }
            return merged;
        }

        private static bool SameDeclaration(XpUifileSyntax.Rule stockRule, XpUifileSyntax.Declaration d)
        {
            if (stockRule == null) return false;
            string st = stockRule.Get(d.Property);
            return st != null && string.Equals(st, d.RawValue, StringComparison.OrdinalIgnoreCase);
        }
    }
}
