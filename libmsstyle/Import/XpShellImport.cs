using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace libmsstyle.Import
{
    /// <summary>
    /// Carries a custom xp shellstyle.dll (the task pane) into the win10 port.
    ///
    /// The win10 shellstyle can't hold xp's stylesheet: its UIFILE draws the task pane from the
    /// msstyles ExplorerBar class (gtc/dtb lookups), and the xp ports transcribed stock xp's
    /// shellstyle into those records: every stock luna value below equals the Luna port's
    /// ExplorerBar record, header bitmaps 110/112 pixel for pixel.
    ///
    /// Each record takes what xp's DirectUI resolved for the element it stands for (<see cref="DuiCascade"/>:
    /// shell32's element tree and DirectUI's own lookup rules), not a value read under one selector: themes put
    /// the same look on different elements (the header on the button or on its title, the group fill on the
    /// tasklist, the clipper or nowhere), and only the resolved value says what xp painted. Three-way against the
    /// stock luna shellstyle, resolved the same way: where the source resolves to stock, the template's original
    /// ExplorerBar value stays (or comes back, if the msstyles merge replaced it).
    ///
    /// Bitmaps the win10 shellstyle keeps under xp's ids (10-25, 100-112) are replaced with the
    /// source shellstyle's where those differ from stock, see <see cref="PatchShellStyleBitmaps"/>.
    /// </summary>
    internal class XpShellImport
    {
        private const string Cls = "ExplorerBar";

        private readonly Win7StyleImporter m_imp;
        private readonly XpShellStyle m_src, m_base;
        private readonly DuiCascade m_srcDui, m_baseDui;
        // (part, state, name id) -> template value before any merge.
        private readonly Dictionary<string, StyleProperty> m_snapshot = new Dictionary<string, StyleProperty>();
        private StyleClass m_cls;

        // true: the source ships no shellstyle and m_src is xp's classic System32 one.
        private readonly bool m_classicFallback;

        public XpShellImport(Win7StyleImporter importer, XpShellStyle source, XpShellStyle baseStyle, bool classicFallback = false)
        {
            m_imp = importer;
            m_src = source;
            m_base = baseStyle;
            m_srcDui = new DuiCascade(source);
            m_baseDui = new DuiCascade(baseStyle);
            m_classicFallback = classicFallback;
        }

        private static string K(int part, int state, IDENTIFIER id) => $"{part}/{state}/{(int)id}";

        /// <summary>call before the msstyles merge, so the template's own values can be restored.</summary>
        public void Snapshot()
        {
            if (!m_imp.Target.ClassesByName.TryGetValue(Cls, out m_cls)) return;
            foreach (var part in m_cls.Parts.Values)
                foreach (var st in part.States.Values)
                    foreach (var p in st.Properties)
                    {
                        var copy = new StyleProperty((PropertyHeader)p.Header.Clone());
                        Win7StyleImporter.CopyValue(p, copy);
                        m_snapshot[$"{part.PartId}/{st.StateId}/{p.Header.nameID}"] = copy;
                    }
        }

        // ExplorerBar parts (tmschema EBP_*).
        private const int Background = 0, NormalBg = 5, NormalCollapse = 6, NormalExpand = 7, NormalHead = 8,
            SpecialBg = 9, SpecialCollapse = 10, SpecialExpand = 11, SpecialHead = 12;

        /// <summary>a property of one tree element, resolved in the source and in stock luna.</summary>
        private (string Src, string Stock) Both(Func<DuiCascade.Section, DuiCascade.Node> pick, bool main, string property, bool hot = false, bool open = true)
        {
            var s = DuiCascade.Build(main, hot, open); var b = DuiCascade.Build(main, hot, open);
            return (m_srcDui.Resolve(pick(s), property), m_baseDui.Resolve(pick(b), property));
        }

        public void Run()
        {
            var r = m_imp.Report;
            if (m_cls == null)
            {
                r.Warnings.Add("shellstyle: template has no ExplorerBar class; the task pane keeps the template's look.");
                return;
            }

            // task pane background gradient: the sectionlist element.
            var pane = Both(x => x.Pane, false, "background");
            var sFill = XpShellStyle.ParseFill(pane.Src);
            var bFill = XpShellStyle.ParseFill(pane.Stock);
            if (Usable(sFill, "task pane background"))
            {
                bool stock = SameFill(sFill, bFill);
                SetColor(Background, 0, IDENTIFIER.GRADIENTCOLOR1, stock ? (Color?)null : sFill[0], "task pane background (start)");
                SetColor(Background, 0, IDENTIFIER.GRADIENTCOLOR2, stock ? (Color?)null : sFill[sFill.Length - 1], "task pane background (end)");
            }

            // task pane padding: xp's sectionlist padding is the win10 UIFILE's gtmar(ExplorerBar, 0, 0, 3602)
            // (ExplorerBarBackground padding); stock luna's 12rp on every side equals the Luna port's record.
            var pad = Both(x => x.Pane, false, "padding");
            SetMargins(Background, IDENTIFIER.CONTENTMARGINS, XpShellStyle.ParseRect(pad.Src), XpShellStyle.ParseRect(pad.Stock), "task pane padding");

            // xp's classic shellstyle (no Shell folder): measured on xp x64 sp2 under Flarn, the special group is drawn
            // exactly like the others (buttonface header, buttontext), though its stylesheet asks for activecaption.
            bool specialMain = !m_classicFallback;
            foreach (var (main, collapse, expand, bg, head) in new[]
            {
                (false, NormalCollapse, NormalExpand, NormalBg, NormalHead),
                (specialMain, SpecialCollapse, SpecialExpand, SpecialBg, SpecialHead),
            })
            {
                string what = main ? "special group" : "normal group";
                // header title text (win10 collapse part, 3803) normal / hot; task links (expand part).
                TextColor(collapse, 0, Both(x => x.Title, main, "foreground"), what + " header text");
                TextColor(collapse, 2, Both(x => x.Title, main, "foreground", hot: true), what + " header text (hover)");
                TextColor(expand, 0, Both(x => x.TaskTitle, main, "foreground"), what + " task text");
                TextColor(expand, 2, Both(x => x.TaskTitle, main, "foreground", hot: true), what + " task text (hover)");
                GroupTile(bg, main, what);
                HeaderImage(head, main, what);
                ArrowStrip(collapse, main, true, what);
                ArrowStrip(expand, main, false, what);
            }
            // the win10 UIFILE pads every tasklist with gtmar(ExplorerBar, 5, 0, 3602) (8px in Luna10, whose groups
            // the port fitted to stock luna's tasklist padding rect(12,10,12,10)); the source's difference from stock
            // moves it the same way. Opus OS: rect(3,7,3,7), its tasks sit 9px further left and 3px tighter than luna.
            TaskListInset();
            PaneFonts();
        }

        /// <summary>
        /// the task pane fonts. win10 draws every header title with gtf(TextStyle, 3, 0) and every task link with
        /// gtf(TextStyle, 6, 0/2) (its ExplorerBarStyle sheet; nothing else in the shell's UIFILEs reads those two
        /// parts for long text, the rest are labels in the same role). xp's are the resolved fontface / fontsize /
        /// fontweight / fontstyle of the header title and the task title (rcstr/rcint read the shellstyle's own string
        /// table: SHADED's headers are its rcstr(1) "Swis721 Cn BT D-Type" at rcint(15) 7pt, bold). Where both
        /// resolve to stock luna's font, the template's stays.
        /// </summary>
        private void PaneFonts()
        {
            var ts = m_imp.Target.ClassesByName.TryGetValue("TextStyle", out var c) ? c : null;
            if (ts == null) return;
            PaneFont(ts, 3, 0, x => x.Title, false, "header title");
            PaneFont(ts, 6, 0, x => x.TaskTitle, false, "task link");
            PaneFont(ts, 6, 2, x => x.TaskTitle, true, "task link (hover)");
        }

        private void PaneFont(StyleClass ts, int part, int state, Func<DuiCascade.Section, DuiCascade.Node> pick, bool hot, string what)
        {
            var src = ResolvedFont(m_srcDui, m_src, pick, hot);
            var stock = ResolvedFont(m_baseDui, m_base, pick, hot);
            if (src == null || stock == null || src.Equals(stock)) return;
            if (!ts.Parts.TryGetValue(part, out var p) || !p.States.TryGetValue(state, out var st)) return;
            var prop = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)IDENTIFIER.FONT);
            if (prop == null) return;
            if (!m_imp.PlaceFont(src.ToString(), out int id, out string error)) { m_imp.Report.Warnings.Add($"shellstyle: {what} font not ported: {error}"); return; }
            if ((prop.Header.shortFlag & 0xFFFF) == id) return;
            prop.SetValue(id);
            string partName = SemanticModel.PartNames("TextStyle", Platform.Win10).TryGetValue(part, out var n) ? n : "Part " + part;
            m_imp.MarkTouched(prop, $"TextStyle|{partName}|S{state}|FONT");
            m_imp.Report.Decisions.Add(new MergeDecision
            {
                Class = "TextStyle", Part = partName, State = state, Property = "FONT", Action = "applied-shellstyle",
                Reason = $"the task pane {what} font of the source shellstyle (win10 draws it with this TextStyle part)", Result = src.ToString(),
            });
            m_imp.Report.Count("applied-shellstyle");
        }

        /// <summary>DUI fontface/fontsize/fontweight/fontstyle resolved for one element, as a uxtheme font string.</summary>
        private static FontSpec ResolvedFont(DuiCascade dui, XpShellStyle style, Func<DuiCascade.Section, DuiCascade.Node> pick, bool hot)
        {
            var node = pick(DuiCascade.Build(false, hot));
            string face = ResText(style, dui.Resolve(node, "fontface"));
            string size = dui.Resolve(node, "fontsize"), weight = dui.Resolve(node, "fontweight"), fstyle = dui.Resolve(node, "fontstyle");
            if (face == null || size == null) return null;
            // a face the author's tooling mangled into question marks (Lion OS X ships "????" in its string table):
            // CreateFont falls back to the system font for it, which isn't a font choice to carry over.
            if (face.Trim().Trim('?').Length == 0) return null;
            // "rcint(15)pt" / "8pt": points (parse.y: magnitude YYPT -> PointToPixel(n, dpi)).
            var sm = System.Text.RegularExpressions.Regex.Match(size.Trim(), @"^(?:rcint\((\d+)\)|(\d+))\s*pt$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!sm.Success) return null;
            string pts = sm.Groups[1].Success ? style.ResString(int.Parse(sm.Groups[1].Value)) : sm.Groups[2].Value;
            if (!int.TryParse(pts?.Trim(), out int points) || points <= 0) return null;
            int w = 400;
            var wm = weight == null ? null : System.Text.RegularExpressions.Regex.Match(weight.Trim(), @"^(?:rcint\((\d+)\)|(\d+))$");
            if (wm != null && wm.Success) int.TryParse(wm.Groups[1].Success ? style.ResString(int.Parse(wm.Groups[1].Value)) : wm.Groups[2].Value, out w);
            else if (weight != null && weight.Trim().Equals("bold", StringComparison.OrdinalIgnoreCase)) w = 700;
            var parts = new List<string> { face.Trim(), points.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (w >= 600) parts.Add("Bold");
            if (fstyle != null && fstyle.IndexOf("underline", StringComparison.OrdinalIgnoreCase) >= 0) parts.Add("Underline");
            if (fstyle != null && fstyle.IndexOf("italic", StringComparison.OrdinalIgnoreCase) >= 0) parts.Add("Italic");
            var f = FontSpec.Parse(string.Join(", ", parts));
            return f.Error == null && f.IsSane ? f : null;
        }

        /// <summary>a DUI string value: rcstr(n) from the shellstyle's string table, a quoted or bare literal otherwise.</summary>
        private static string ResText(XpShellStyle style, string v)
        {
            if (v == null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(v.Trim(), @"^rcstr\((\d+)\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return style.ResString(int.Parse(m.Groups[1].Value));
            if (v.Trim().StartsWith("sysmetricstr", StringComparison.OrdinalIgnoreCase)) return null; // a system font: not the shellstyle's.
            return v.Trim().Trim('"', '\'');
        }

        private void TaskListInset()
        {
            var p = Find(NormalBg, 0, IDENTIFIER.CONTENTMARGINS);
            if (p == null || !m_snapshot.TryGetValue(K(NormalBg, 0, IDENTIFIER.CONTENTMARGINS), out var orig) || !(orig.GetValue() is Margins t)) return;
            var s = XpShellStyle.ParseRect(m_srcDui.Resolve(DuiCascade.Build(false).TaskList, "padding")) ?? new Margins(0, 0, 0, 0);
            var b = XpShellStyle.ParseRect(m_baseDui.Resolve(DuiCascade.Build(false).TaskList, "padding")) ?? new Margins(0, 0, 0, 0);
            var m = new Margins(Math.Max(0, t.Left + s.Left - b.Left), Math.Max(0, t.Top + s.Top - b.Top), Math.Max(0, t.Right + s.Right - b.Right), Math.Max(0, t.Bottom + s.Bottom - b.Bottom));
            if (p.GetValue() is Margins cur && cur.Left == m.Left && cur.Top == m.Top && cur.Right == m.Right && cur.Bottom == m.Bottom) return;
            if (m.Left == t.Left && m.Top == t.Top && m.Right == t.Right && m.Bottom == t.Bottom) { Restore(p, NormalBg, 0, IDENTIFIER.CONTENTMARGINS, "task list padding"); return; }
            p.SetValue(m);
            Changed(p, NormalBg, 0, "applied-shellstyle", $"task list padding differs from stock luna by {s.Left - b.Left},{s.Top - b.Top},{s.Right - b.Right},{s.Bottom - b.Bottom}px (l,t,r,b)");
        }

        private void TextColor(int part, int state, (string Src, string Stock) v, string what)
        {
            var s = XpShellStyle.ParseColor(v.Src);
            var b = XpShellStyle.ParseColor(v.Stock);
            if (s == null) return;
            SetColor(part, state, IDENTIFIER.TEXTCOLOR, SameColor(s, b) ? (Color?)null : s.Value, what);
        }

        private bool Usable(Color[] fill, string what)
        {
            if (fill != null) return true;
            m_imp.Report.Warnings.Add($"shellstyle: {what} isn't a color or gradient the porter reads; template kept.");
            return false;
        }

        private static bool SameColor(Color? a, Color? b) => a.HasValue && b.HasValue && a.Value.ToArgb() == b.Value.ToArgb();
        private static bool SameFill(Color[] a, Color[] b) => a != null && b != null && a.Length == b.Length && a.Zip(b, (x, y) => x.ToArgb() == y.ToArgb()).All(x => x);

        private StyleProperty Find(int part, int state, IDENTIFIER id)
        {
            return m_cls.Parts.TryGetValue(part, out var p) && p.States.TryGetValue(state, out var s)
                ? s.Properties.FirstOrDefault(x => x.Header.nameID == (int)id) : null;
        }

        /// <summary>value null: the source shellstyle is stock here, so the template's original value.</summary>
        private void SetColor(int part, int state, IDENTIFIER id, Color? value, string from)
        {
            var p = Find(part, state, id);
            if (p == null) return;
            if (value == null) { Restore(p, part, state, id, from); return; }
            var c = Color.FromArgb(255, value.Value);
            if (p.GetValue() is Color cur && cur.ToArgb() == c.ToArgb()) return;
            p.SetValue(c);
            Changed(p, part, state, "applied-shellstyle", $"from the source shellstyle's {from}");
        }

        /// <summary>source null: the shellstyle doesn't set it, template kept. Equal to stock: the template's own value.</summary>
        private void SetMargins(int part, IDENTIFIER id, Margins source, Margins stock, string from)
        {
            var p = Find(part, 0, id);
            if (p == null || source == null) return;
            if (stock != null && source.Left == stock.Left && source.Top == stock.Top && source.Right == stock.Right && source.Bottom == stock.Bottom)
            {
                Restore(p, part, 0, id, from);
                return;
            }
            if (p.GetValue() is Margins cur && cur.Left == source.Left && cur.Top == source.Top && cur.Right == source.Right && cur.Bottom == source.Bottom) return;
            p.SetValue(new Margins(source.Left, source.Top, source.Right, source.Bottom));
            Changed(p, part, 0, "applied-shellstyle", $"from the source shellstyle's {from}");
        }

        private void Restore(StyleProperty p, int part, int state, IDENTIFIER id, string from)
        {
            if (!m_snapshot.TryGetValue(K(part, state, id), out var orig)) return;
            if (SemanticModel.FormatValue(orig) == SemanticModel.FormatValue(p)) return;
            Win7StyleImporter.CopyValue(orig, p);
            Changed(p, part, state, "restored-shellstyle", $"source shellstyle keeps the stock {from}; xp drew this from the shellstyle, so the template's value");
        }

        private void Changed(StyleProperty p, int part, int state, string action, string reason)
        {
            string partName = SemanticModel.PartNames(Cls, Platform.Win10).TryGetValue(part, out var n) ? n : "Part " + part;
            string prop = SemanticModel.PropertyName(p.Header.nameID);
            // the validator matches changed records by this label (class|part name|state|property).
            m_imp.MarkTouched(p, $"{Cls}|{partName}|S{state}|{prop}");
            m_imp.Report.Decisions.Add(new MergeDecision
            {
                Class = Cls, Part = partName, State = state, Property = prop, Action = action, Reason = reason,
                Result = SemanticModel.FormatValue(p),
            });
            m_imp.Report.Count(action);
        }

        /// <summary>
        /// The group background (win10 dtb(ExplorerBar, 5/9) on the clipper): what xp showed behind the tasks (the first
        /// opaque fill of tasklist, watermark, clipper, expando and pane, background inheriting through), framed by the
        /// tasklist border where its borderthickness draws one (luna rect(1,0,1,1)). A solid fill is a 10x10 nine-grid
        /// tile, a gradient (Opus OS paints its groups with the pane's gradient) a stretched strip.
        /// </summary>
        private void GroupTile(int part, bool main, string what)
        {
            var img = Find(part, 0, IDENTIFIER.IMAGEFILE);
            if (img == null) return;
            var src = GroupLook(m_srcDui, main);
            var stock = GroupLook(m_baseDui, main);
            if (src.Key == stock.Key) { Restore(img, part, 0, IDENTIFIER.IMAGEFILE, what + " background"); RestoreGeometry(part); return; }
            if (src.Fill == null) { m_imp.Report.Warnings.Add($"shellstyle: {what} background \"{src.Key}\" isn't a color or gradient the porter reads; template tile kept."); return; }

            PlaceImage(img, part, ImageIdentity.EncodePngVerified(Paint(src.Fill, src.Border, src.Thickness, out var sizing)), $"{what} background {src.Key}");
            if (sizing == null) RestoreGeometry(part); // the tile's 1px frame stays a frame with the luna port's 1px margins.
            else SetGeometry(part, sizing, "group background is a gradient, stretched");
            // FILLCOLOR is what win10 fills the group with where the picture isn't used; a see-through group has none.
            if (Find(part, 0, IDENTIFIER.FILLCOLOR) != null && src.Fill.Value.Opaque) SetColor(part, 0, IDENTIFIER.FILLCOLOR, src.Fill.Value.Stops[0], what + " background");
        }

        /// <summary>
        /// the fill xp painted behind a group's tasks: the first element from the tasklist out to the expando that sets
        /// an opaque background itself (an inherited value is the ancestor's, checked in its own turn). None, or the
        /// pane's own fill again (Opus OS paints its clippers with the pane gradient; measured on xp: the same pixels
        /// at the same x in and between groups): the group is see-through, a transparent tile, and win10's clipper
        /// shows its ExplorerBarBackground the same way. A re-drawn gradient would restart at each group's edge.
        /// </summary>
        private (DuiFill? Fill, Color Border, Margins Thickness, string Key) GroupLook(DuiCascade dui, bool main)
        {
            var s = DuiCascade.Build(main);
            var pane = XpShellStyle.ParseFill(dui.Resolve(s.Pane, "background"));
            DuiFill? fill = DuiFill.Clear; string fillText = "transparent";
            foreach (var node in new[] { s.TaskList, s.Watermark, s.Clipper, s.Expando })
            {
                if (node == null) continue;
                var v = dui.Resolve(node, "background", out var origin);
                if (v == null || origin != node) continue;
                var f = DuiFill.Parse(v);
                if (f == null) { fill = null; fillText = v; break; } // a bitmap or something unparsable: reported, not guessed.
                if (!f.Value.Opaque) continue;
                if (f.Value.Stops.Length > 1 && SameFill(f.Value.Stops, pane)) break; // the pane's gradient again: see-through
                fill = f; fillText = v; break;
            }
            var thick = XpShellStyle.ParseRect(dui.Resolve(s.TaskList, "borderthickness")) ?? new Margins(0, 0, 0, 0);
            var border = XpShellStyle.ParseColor(dui.Resolve(s.TaskList, "bordercolor")) ?? Color.Black; // DUI default SC_Black
            bool framed = thick.Left + thick.Top + thick.Right + thick.Bottom > 0;
            string key = $"{fillText}|{(framed ? $"{border.ToArgb()}|{thick.Left},{thick.Top},{thick.Right},{thick.Bottom}" : "")}";
            return (fill, border, thick, key);
        }

        /// <summary>a solid color or gradient(c1, c2, mode) fill as DirectUI paints it (render.cpp: GradientFill across the element).</summary>
        private struct DuiFill
        {
            public Color[] Stops;
            public bool Vertical;
            public bool Opaque => Stops.Length > 1 || Stops[0].A == 255; // gradients are drawn opaque (stops' alpha ignored).
            /// <summary>nothing painted: the element shows what's behind it.</summary>
            public static DuiFill Clear => new DuiFill { Stops = new[] { Color.FromArgb(0, 0, 0, 0) } };
            public static DuiFill? Parse(string v)
            {
                var f = XpShellStyle.ParseFill(v);
                if (f == null) return null;
                return new DuiFill { Stops = f, Vertical = XpShellStyle.GradientMode(v) == 1 };
            }
        }

        /// <summary>
        /// the picture for a fill with a border: solid -> 10x10 tile, <paramref name="sizing"/> null (caller's geometry);
        /// gradient -> 200x20 strip drawn like GradientFill, sizing = the border so it stays a frame when stretched.
        /// </summary>
        private static ArgbImage Paint(DuiFill? fill, Color border, Margins thick, out Margins sizing)
        {
            var f = fill.Value;
            bool grad = f.Stops.Length > 1;
            int w = grad ? 200 : 10, h = grad ? 20 : 10, cap = grad ? 8 : 4;
            int l = Math.Min(thick.Left, cap), t = Math.Min(thick.Top, cap), r = Math.Min(thick.Right, cap), b = Math.Min(thick.Bottom, cap);
            var pic = new ArgbImage(w, h);
            for (int y = 0; y < h; ++y)
                for (int x = 0; x < w; ++x)
                {
                    Color c;
                    if (x < l || x >= w - r || y < t || y >= h - b) c = border;
                    else if (!grad) c = f.Stops[0];
                    else
                    {
                        double k = f.Vertical ? y / (double)(h - 1) : x / (double)(w - 1);
                        Color c0 = f.Stops[0], c1 = f.Stops[f.Stops.Length - 1];
                        c = Color.FromArgb(255, (int)Math.Round(c0.R + (c1.R - c0.R) * k), (int)Math.Round(c0.G + (c1.G - c0.G) * k), (int)Math.Round(c0.B + (c1.B - c0.B) * k));
                    }
                    pic.Pixels[y * w + x] = Premultiplied(c);
                }
            sizing = grad ? new Margins(l, t, r, b) : null;
            return pic;
        }

        private void RestoreGeometry(int part)
        {
            foreach (var id in new[] { IDENTIFIER.SIZINGMARGINS, IDENTIFIER.IMAGECOUNT, IDENTIFIER.IMAGELAYOUT })
            {
                var p = Find(part, 0, id);
                if (p != null) Restore(p, part, 0, id, "tile geometry");
            }
        }

        private static uint Premultiplied(Color c)
        {
            uint a = c.A;
            return (a << 24) | ((uint)c.R * a / 255 << 16) | ((uint)c.G * a / 255 << 8) | ((uint)c.B * a / 255);
        }

        /// <summary>
        /// The header (win10 dtb(ExplorerBar, 8/12) behind the title) is what xp showed there: the title's own
        /// background over the header button's, over what's behind the button (the wrapper or the expando, where the
        /// header is see-through). A bitmap becomes the header picture, a solid color a flat 10x10 tile; a gradient
        /// (Opus OS) is drawn into a header-sized picture the way DirectUI fills it.
        /// </summary>
        private void HeaderImage(int part, bool main, string what)
        {
            var img = Find(part, 0, IDENTIFIER.IMAGEFILE);
            if (img == null) return;
            string sBack = HeaderBackground(m_srcDui, main), bBack = HeaderBackground(m_baseDui, main);
            if (XpShellStyle.ParseRcBmp(sBack, out int id, out var key))
            {
                XpShellStyle.ParseRcBmp(bBack, out int bid, out _);
                var dib = m_src.GetBitmap(id);
                if (dib == null) { m_imp.Report.Warnings.Add($"shellstyle: {what} header bitmap {id} is missing; template kept."); return; }
                if (bid > 0 && SameBytes(dib, m_base.GetBitmap(bid))) { Restore(img, part, 0, IDENTIFIER.IMAGEFILE, what + " header"); RestoreHeaderGeometry(part); HeaderInset(part, main, what); return; }
                // mode 6 color keyed; otherwise DirectUI's per-pixel alpha rule.
                var png = ImageConverter.ConvertXpDibToPremultipliedPng(dib, key, key == null);
                var pic = ImageIdentity.Decode(png);
                // a one-color bitmap (Lion's 18x18 title tile) is a flat header: cut to the template's header margins
                // it would be narrower than them.
                if (pic.Pixels.All(p => p == pic.Pixels[0]) && (pic.Pixels[0] >> 24) == 0xFF)
                {
                    FlatHeader(part, img, Color.FromArgb((int)((pic.Pixels[0] >> 16) & 0xFF), (int)((pic.Pixels[0] >> 8) & 0xFF), (int)(pic.Pixels[0] & 0xFF)), $"{what} header bitmap {id}");
                    HeaderInset(part, main, what);
                    return;
                }
                PlaceImage(img, part, png, $"{what} header bitmap {id}");
                RestoreHeaderGeometry(part);
                HeaderInset(part, main, what);
                return;
            }
            var fill = sBack == "transparent" ? DuiFill.Clear : DuiFill.Parse(sBack);
            if (fill == null) { m_imp.Report.Warnings.Add($"shellstyle: {what} header background \"{sBack}\" isn't a color, gradient or bitmap the porter reads; template kept."); return; }
            // the header button's own border (Opus OS: a 1px line under each header, borderthickness rect(0,0,0,1)).
            var sec = DuiCascade.Build(main);
            var thick = XpShellStyle.ParseRect(m_srcDui.Resolve(sec.Header, "borderthickness")) ?? new Margins(0, 0, 0, 0);
            var border = XpShellStyle.ParseColor(m_srcDui.Resolve(sec.Header, "bordercolor")) ?? Color.Black;
            var bsec = DuiCascade.Build(main);
            if (sBack == bBack && SameRect(thick, XpShellStyle.ParseRect(m_baseDui.Resolve(bsec.Header, "borderthickness"))))
            { Restore(img, part, 0, IDENTIFIER.IMAGEFILE, what + " header"); RestoreHeaderGeometry(part); HeaderInset(part, main, what); return; }
            var painted = Paint(fill, border, thick, out var sizing);
            PlaceImage(img, part, ImageIdentity.EncodePngVerified(painted), $"{what} header {sBack}");
            // solid: a 10x10 tile stretched with 1px margins, its 1px border (if any) staying on its edge.
            SetGeometry(part, sizing ?? new Margins(1, 1, 1, 1), sizing != null ? "header is a gradient, stretched" : "header is a flat color, a 10x10 tile");
            HeaderInset(part, main, what);
        }

        private static bool SameRect(Margins a, Margins b) => (a ?? new Margins(0, 0, 0, 0)) is Margins x && (b ?? new Margins(0, 0, 0, 0)) is Margins y
            && x.Left == y.Left && x.Top == y.Top && x.Right == y.Right && x.Bottom == y.Bottom;

        /// <summary>
        /// the background xp painted behind the header title: the first element from the title out to the expando that
        /// sets one itself (an inherited value is the ancestor's, checked in its turn). None: the header is see-through
        /// and shows the pane (Opus OS), "transparent".
        /// </summary>
        private static string HeaderBackground(DuiCascade dui, bool main)
        {
            var s = DuiCascade.Build(main);
            foreach (var node in new[] { s.Title, s.Header, s.Wrapper, s.Expando })
            {
                if (node == null) continue;
                var v = dui.Resolve(node, "background", out var origin);
                if (v == null || origin != node) continue;
                if (XpShellStyle.ParseRcBmp(v, out _, out _)) return v;
                var f = DuiFill.Parse(v);
                if (f == null || f.Value.Opaque) return v;
            }
            return "transparent";
        }

        private void FlatHeader(int part, StyleProperty img, Color c, string from)
        {
            var tile = new ArgbImage(10, 10);
            for (int i = 0; i < tile.Pixels.Length; ++i) tile.Pixels[i] = Premultiplied(Color.FromArgb(255, c));
            PlaceImage(img, part, ImageIdentity.EncodePngVerified(tile), from);
            SetGeometry(part, new Margins(1, 1, 1, 1), "header is a flat color, a 10x10 tile");
        }

        private void SetGeometry(int part, Margins sizing, string reason)
        {
            foreach (var gid in new[] { IDENTIFIER.SIZINGMARGINS, IDENTIFIER.IMAGECOUNT })
            {
                var gp = Find(part, 0, gid);
                if (gp == null) continue;
                string want = gid == IDENTIFIER.IMAGECOUNT ? "1" : $"(l{sizing.Left},t{sizing.Top},r{sizing.Right},b{sizing.Bottom})";
                if (SemanticModel.FormatValue(gp) == want) continue;
                if (gid == IDENTIFIER.IMAGECOUNT) gp.SetValue(1); else gp.SetValue(new Margins(sizing.Left, sizing.Top, sizing.Right, sizing.Bottom));
                Changed(gp, part, 0, "applied-shellstyle", reason);
            }
        }

        // the header strip is one frame stretched with the margins the port gave the shellstyle bitmap.
        private void RestoreHeaderGeometry(int part)
        {
            foreach (var id in new[] { IDENTIFIER.SIZINGMARGINS, IDENTIFIER.CONTENTMARGINS, IDENTIFIER.IMAGECOUNT })
            {
                var p = Find(part, 0, id);
                if (p != null) Restore(p, part, 0, id, "header geometry");
            }
        }

        /// <summary>
        /// xp sizes the header button by its borderthickness + padding around the title; win10's UIFILE pads it
        /// with gtmar(ExplorerBar, 8/12, 0, 3602). The port fitted that record to stock luna (borderthickness 2,
        /// padding 0 top/bottom), so the source's difference from stock is added to the template's margins:
        /// TangoLux's borderthickness 0 makes its headers 2px shorter, as on xp. Both are resolved on the header
        /// button (neither inherits).
        /// </summary>
        private void HeaderInset(int part, bool main, string what)
        {
            var p = Find(part, 0, IDENTIFIER.CONTENTMARGINS);
            if (p == null || !m_snapshot.TryGetValue(K(part, 0, IDENTIFIER.CONTENTMARGINS), out var orig) || !(orig.GetValue() is Margins t)) return;
            // where the title text starts inside the header: the button's border and padding, then the title's own
            // padding (Lion OS X pads the title 10rp, not the button; Opus OS the button 1rp; luna the button 2 + 10).
            Margins Inset(DuiCascade d)
            {
                var s = DuiCascade.Build(main);
                var b = XpShellStyle.ParseRect(d.Resolve(s.Header, "borderthickness")) ?? new Margins(0, 0, 0, 0);
                var pd = XpShellStyle.ParseRect(d.Resolve(s.Header, "padding")) ?? new Margins(0, 0, 0, 0);
                var tp = XpShellStyle.ParseRect(d.Resolve(s.Title, "padding")) ?? new Margins(0, 0, 0, 0);
                return new Margins(b.Left + pd.Left + tp.Left, b.Top + pd.Top + tp.Top, b.Right + pd.Right + tp.Right, b.Bottom + pd.Bottom + tp.Bottom);
            }
            var si = Inset(m_srcDui); var bi = Inset(m_baseDui);
            // the left inset moves the title like on xp (Opus OS: its titles sit 11px left of luna's, measured on the
            // xp vm). The template's margin keeps its own share (room for win10's header icon), so this is a difference.
            // A title pushed out past the header (Lion's special group: padding 200rp in a 200rp-wide section, its
            // title hidden on purpose) isn't a placement win10 can show; the template's margin stays then.
            int left = si.Left - bi.Left > 64 ? t.Left : Math.Max(0, t.Left + si.Left - bi.Left);
            int top = Math.Max(0, t.Top + si.Top - bi.Top), bottom = Math.Max(0, t.Bottom + si.Bottom - bi.Bottom);
            if (left == t.Left && top == t.Top && bottom == t.Bottom) return;
            if (p.GetValue() is Margins cur && cur.Top == top && cur.Bottom == bottom && cur.Left == left && cur.Right == t.Right) return;
            p.SetValue(new Margins(left, top, t.Right, bottom));
            Changed(p, part, 0, "applied-shellstyle", $"{what} header title inset differs from stock luna by {si.Left - bi.Left}px left, {si.Top - bi.Top}px top, {si.Bottom - bi.Bottom}px bottom");
        }

        /// <summary>
        /// the arrow strip, 3 frames normal/hot/pressed, from the arrow element's content; collapse is the part shown
        /// while the group is open (xp: the expando is selected), expand while it's closed. pressed = hot, as on xp.
        /// </summary>
        private void ArrowStrip(int part, bool main, bool open, string what)
        {
            var img = Find(part, 0, IDENTIFIER.IMAGEFILE);
            if (img == null) return;
            var src = new List<(byte[] Dib, Color? Key, bool AutoKey)>();
            bool stock = true;
            foreach (bool hot in new[] { false, true, true })
            {
                var v = Both(x => x.Arrow, main, "content", hot, open);
                if (!XpShellStyle.ParseRcBmp(v.Src, out int id, out var key, out bool autoKey)) return;
                XpShellStyle.ParseRcBmp(v.Stock, out int bid, out _);
                var dib = m_src.GetBitmap(id);
                if (dib == null) { m_imp.Report.Warnings.Add($"shellstyle: {what} arrow bitmap {id} is missing; template kept."); return; }
                stock &= bid > 0 && SameBytes(dib, m_base.GetBitmap(bid));
                src.Add((dib, key, autoKey));
            }
            if (stock)
            {
                Restore(img, part, 0, IDENTIFIER.IMAGEFILE, what + " arrows");
                var ic = Find(part, 0, IDENTIFIER.IMAGECOUNT);
                if (ic != null) Restore(ic, part, 0, IDENTIFIER.IMAGECOUNT, what + " arrows");
                return;
            }
            // rcbmp(id): color key = the bitmap's top-left pixel (xp's classic arrows, magenta).
            // rcbmp(id, 6, key): that key. rcbmp(id, 2): DirectUI per-pixel alpha, any non-zero alpha counts.
            var pics = src.Select(s =>
            {
                if (!s.AutoKey) return ImageIdentity.Decode(ImageConverter.ConvertXpDibToPremultipliedPng(s.Dib, s.Key, s.Key == null));
                var opaque = ImageIdentity.Decode(ImageConverter.ConvertXpDibToPremultipliedPng(s.Dib, null));
                uint p0 = opaque.Pixels[0];
                return ImageIdentity.Decode(ImageConverter.ConvertXpDibToPremultipliedPng(s.Dib, Color.FromArgb((int)((p0 >> 16) & 0xFF), (int)((p0 >> 8) & 0xFF), (int)(p0 & 0xFF))));
            }).ToList();
            int w = pics[0].Width, h = pics[0].Height;
            if (pics.Any(p => p.Width != w || p.Height != h))
            {
                m_imp.Report.Warnings.Add($"shellstyle: {what} arrow bitmaps differ in size; template kept.");
                return;
            }
            var strip = new ArgbImage(w, h * pics.Count);
            for (int i = 0; i < pics.Count; ++i) strip.Blit(pics[i], 0, i * h);
            PlaceImage(img, part, ImageIdentity.EncodePngVerified(strip), $"{what} arrow bitmaps (normal, hot, pressed)");
            var count = Find(part, 0, IDENTIFIER.IMAGECOUNT);
            if (count != null && !(count.GetValue() is int c && c == pics.Count)) { count.SetValue(pics.Count); Changed(count, part, 0, "applied-shellstyle", "frame count of the arrow strip"); }
        }

        private void PlaceImage(StyleProperty p, int part, byte[] png, string from)
        {
            int id = m_imp.PlaceResource("IMAGE", -1, png, ImageIdentity.Analyze(png));
            if ((p.Header.shortFlag & 0xFFFF) == id) return;
            p.SetValue(id);
            Changed(p, part, 0, "applied-shellstyle", "from the source shellstyle's " + from);
        }

        private static bool SameBytes(byte[] a, byte[] b) => a != null && b != null && a.SequenceEqual(b);

        /// <summary>
        /// The win10 shellstyle keeps xp's RT_BITMAP ids (folder watermarks 10-25, task pane art
        /// 100-112) for whoever still loads them (xp appwiz, shell32's legacy paths). Each id both
        /// shellstyles have gets the source's bitmap where it differs from stock. Returns the ids replaced.
        /// </summary>
        public static List<int> PatchShellStyleBitmaps(string win10ShellStyle, XpShellStyle source, XpShellStyle stock, bool dryRun)
        {
            var replace = new SortedDictionary<int, byte[]>();
            HashSet<int> present;
            using (var target = new XpBitmapModule(win10ShellStyle)) present = target.Ids;
            foreach (int id in source.BitmapIds().Distinct())
            {
                if (!present.Contains(id)) continue;
                var s = source.GetBitmap(id);
                if (s == null || SameBytes(s, stock.GetBitmap(id))) continue;
                replace[id] = s;
            }
            if (dryRun || replace.Count == 0) return replace.Keys.ToList();

            IntPtr h = Win32Api.BeginUpdateResource(win10ShellStyle, false);
            if (h == IntPtr.Zero) throw new IOException($"can't open \"{win10ShellStyle}\" for resource updates");
            bool ok = true;
            foreach (var kv in replace)
                ok &= Win32Api.UpdateResource(h, 2u, (uint)kv.Key, 1033, kv.Value, (uint)kv.Value.Length);
            if (!Win32Api.EndUpdateResource(h, !ok) || !ok) throw new IOException($"updating bitmaps in \"{win10ShellStyle}\" failed");
            return replace.Keys.ToList();
        }

        /// <summary>RT_BITMAP ids of a vista+ shellstyle (it has no UIFILE 1 in xp syntax, so no XpShellStyle).</summary>
        private class XpBitmapModule : IDisposable
        {
            private IntPtr m_h;
            public HashSet<int> Ids { get; } = new HashSet<int>();
            public XpBitmapModule(string path)
            {
                m_h = Win32Api.LoadLibraryEx(path, IntPtr.Zero, Win32Api.LoadLibraryFlags.LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE);
                if (m_h == IntPtr.Zero) throw new IOException($"can't open \"{path}\"");
                Win32Api.EnumResourceNamesEx(m_h, 2, (m, t, name, l) =>
                {
                    long v = name.ToInt64();
                    if (v > 0 && v <= 0xFFFF) Ids.Add((int)v);
                    return true;
                }, IntPtr.Zero, Win32Api.EnumResourceFlags.RESOURCE_ENUM_LN, 0);
            }
            public void Dispose() { if (m_h != IntPtr.Zero) Win32Api.FreeLibrary(m_h); m_h = IntPtr.Zero; }
        }

        /// <summary>Shell\&lt;scheme&gt;\shellstyle.dll next to an xp style, folder and file names matched without case. Null when absent.</summary>
        public static string FindShellStyle(string stylePath, string scheme)
        {
            string shell = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(stylePath)), "Shell");
            if (!Directory.Exists(shell) || string.IsNullOrEmpty(scheme)) return null;
            var dir = Directory.GetDirectories(shell).FirstOrDefault(d => string.Equals(Path.GetFileName(d), scheme, StringComparison.OrdinalIgnoreCase));
            return dir == null ? null : Directory.GetFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), "shellstyle.dll", StringComparison.OrdinalIgnoreCase));
        }
    }
}
