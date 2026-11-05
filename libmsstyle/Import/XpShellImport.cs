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
    /// ExplorerBar record, header bitmaps 110/112 pixel for pixel. So the source shellstyle's values
    /// go into the same records. Three-way, against the stock luna shellstyle: where the source
    /// shellstyle is stock, the template's original ExplorerBar value stays (or comes back, if the
    /// msstyles merge replaced it, since on xp these parts were drawn from the shellstyle).
    ///
    /// Bitmaps the win10 shellstyle keeps under xp's ids (10-25, 100-112) are replaced with the
    /// source shellstyle's where those differ from stock, see <see cref="PatchShellStyleBitmaps"/>.
    /// </summary>
    internal class XpShellImport
    {
        private const string Cls = "ExplorerBar";

        private readonly Win7StyleImporter m_imp;
        private readonly XpShellStyle m_src, m_base;
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

        // win10 ExplorerBar text color <- xp stylesheet / selector (the win10 UIFILE reads part/state 3803).
        private static readonly (int Part, int State, string Resid, string Selector)[] TextColors =
        {
            (NormalCollapse, 0, "sectionss", "button"),
            (NormalCollapse, 2, "sectionss", "element [id=atom(title)][mousefocused]"),
            (NormalExpand, 0, "sectiontaskss", "button"),
            (NormalExpand, 2, "sectiontaskss", "element [id=atom(title)][mousefocused]"),
            (SpecialCollapse, 0, "mainsectionss", "button [id=atom(header)]"),
            (SpecialCollapse, 2, "mainsectionss", "element [id=atom(title)][mousefocused]"),
            (SpecialExpand, 0, "mainsectiontaskss", "button"),
            (SpecialExpand, 2, "mainsectiontaskss", "element [id=atom(title)][mousefocused]"),
        };

        // arrow strips: 3 frames normal/hot/pressed; collapse is shown while the group is open (xp [selected]).
        private static readonly (int Part, string Resid, string[] Frames)[] Arrows =
        {
            (NormalCollapse, "sectionss", new[] { "element [id=atom(arrow)][selected]", "element [id=atom(arrow)][selected][mousefocused]", "element [id=atom(arrow)][selected][mousefocused]" }),
            (NormalExpand, "sectionss", new[] { "element [id=atom(arrow)]", "element [id=atom(arrow)][mousefocused]", "element [id=atom(arrow)][mousefocused]" }),
            (SpecialCollapse, "mainsectionss", new[] { "element [id=atom(arrow)][selected]", "element [id=atom(arrow)][selected][mousefocused]", "element [id=atom(arrow)][selected][mousefocused]" }),
            (SpecialExpand, "mainsectionss", new[] { "element [id=atom(arrow)]", "element [id=atom(arrow)][mousefocused]", "element [id=atom(arrow)][mousefocused]" }),
        };

        public void Run()
        {
            var r = m_imp.Report;
            if (m_cls == null)
            {
                r.Warnings.Add("shellstyle: template has no ExplorerBar class; the task pane keeps the template's look.");
                return;
            }

            // task pane background gradient
            var sFill = XpShellStyle.ParseFill(m_src.Get("taskpane", "element [id=atom(sectionlist)]", "background"));
            var bFill = XpShellStyle.ParseFill(m_base.Get("taskpane", "element [id=atom(sectionlist)]", "background"));
            if (Usable(sFill, "task pane background"))
            {
                bool stock = SameFill(sFill, bFill);
                SetColor(Background, 0, IDENTIFIER.GRADIENTCOLOR1, stock ? (Color?)null : sFill[0], "task pane background (start)");
                SetColor(Background, 0, IDENTIFIER.GRADIENTCOLOR2, stock ? (Color?)null : sFill[sFill.Length - 1], "task pane background (end)");
            }

            // task pane padding: xp's sectionlist padding is the win10 UIFILE's gtmar(ExplorerBar, 0, 0, 3602)
            // (ExplorerBarBackground padding); stock luna's 12rp on every side equals the Luna port's record.
            SetMargins(Background, IDENTIFIER.CONTENTMARGINS,
                XpShellStyle.ParseRect(m_src.Get("taskpane", "element [id=atom(sectionlist)]", "padding")),
                XpShellStyle.ParseRect(m_base.Get("taskpane", "element [id=atom(sectionlist)]", "padding")), "task pane padding");

            foreach (var t in TextColors)
            {
                var s = XpShellStyle.ParseColor(m_src.Get(t.Resid, t.Selector, "foreground"));
                var b = XpShellStyle.ParseColor(m_base.Get(t.Resid, t.Selector, "foreground"));
                // no hover rule (xp's classic shellstyle): the title keeps its normal color on hover, so the
                // hover state takes the normal state's source color instead of keeping the template's blue.
                if (s == null && t.State == 2)
                {
                    var normal = TextColors.First(n => n.Part == t.Part && n.State == 0);
                    s = XpShellStyle.ParseColor(m_src.Get(normal.Resid, normal.Selector, "foreground"));
                    b = null;
                }
                if (s == null) continue;
                SetColor(t.Part, t.State, IDENTIFIER.TEXTCOLOR, SameColor(s, b) ? (Color?)null : s.Value, $"{t.Resid} {t.Selector} foreground");
            }

            GroupTile(NormalBg, "sectionss");
            HeaderImage(NormalHead, "sectionss", "button");
            if (m_classicFallback)
            {
                // measured on xp x64 sp2 under Flarn (no Shell folder): the special group is drawn exactly like the
                // others, buttonface header with buttontext, buttonface border, though the classic stylesheet asks
                // for activecaption. so it takes the normal section's look.
                GroupTile(SpecialBg, "sectionss");
                HeaderImage(SpecialHead, "sectionss", "button");
                var fg = XpShellStyle.ParseColor(m_src.Get("sectionss", "button", "foreground"));
                if (fg != null) { SetColor(SpecialCollapse, 0, IDENTIFIER.TEXTCOLOR, fg, "sectionss button foreground"); SetColor(SpecialCollapse, 2, IDENTIFIER.TEXTCOLOR, fg, "sectionss button foreground"); }
            }
            else
            {
                GroupTile(SpecialBg, "mainsectionss", "element [id=atom(watermark)]");
                HeaderImage(SpecialHead, "mainsectionss", "button [id=atom(header)]");
            }
            foreach (var a in Arrows) ArrowStrip(a.Part, a.Resid, a.Frames);
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
        /// The group background is a 10x10 nine-grid tile: fill inside, the tasklist border on the
        /// sides xp drew it (borderthickness rect(l,t,r,b), luna rect(1,0,1,1)).
        /// </summary>
        private void GroupTile(int part, string resid, string fillSelector = "tasklist [id=atom(tasklist)]")
        {
            const string list = "tasklist [id=atom(tasklist)]";
            string sBg = m_src.Get(resid, fillSelector, "background"), bBg = m_base.Get(resid, fillSelector, "background");
            string sBc = m_src.Get(resid, list, "bordercolor"), bBc = m_base.Get(resid, list, "bordercolor");
            string sBt = m_src.Get(resid, list, "borderthickness"), bBt = m_base.Get(resid, list, "borderthickness");
            var img = Find(part, 0, IDENTIFIER.IMAGEFILE);
            if (img == null) return;
            if (sBg == bBg && sBc == bBc && sBt == bBt) { Restore(img, part, 0, IDENTIFIER.IMAGEFILE, resid + " group background"); RestoreGeometry(part); return; }

            var fill = XpShellStyle.ParseColor(sBg);
            var border = XpShellStyle.ParseColor(sBc) ?? Color.White;
            var thick = XpShellStyle.ParseRect(sBt) ?? new Margins(0, 0, 0, 0);
            if (fill == null) { m_imp.Report.Warnings.Add($"shellstyle: {resid} group background \"{sBg}\" isn't a color; template tile kept."); return; }

            const int n = 10;
            var tile = new ArgbImage(n, n);
            for (int y = 0; y < n; ++y)
                for (int x = 0; x < n; ++x)
                {
                    bool edge = (x < Math.Min(thick.Left, 4)) || (x >= n - Math.Min(thick.Right, 4)) || (y < Math.Min(thick.Top, 4)) || (y >= n - Math.Min(thick.Bottom, 4));
                    tile.Pixels[y * n + x] = Premultiplied(edge ? border : fill.Value);
                }
            PlaceImage(img, part, ImageIdentity.EncodePngVerified(tile), $"{resid} group background {sBg}, border {sBc} {sBt}");
            // the tile's 1px frame only stays a frame with 1px sizing margins (the luna port's).
            RestoreGeometry(part);
            var fc = Find(part, 0, IDENTIFIER.FILLCOLOR);
            if (fc != null && fill.Value.A == 255) SetColor(part, 0, IDENTIFIER.FILLCOLOR, fill.Value, resid + " group background");
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

        private void HeaderImage(int part, string resid, string selector)
        {
            var img = Find(part, 0, IDENTIFIER.IMAGEFILE);
            if (img == null) return;
            string sBack = m_src.Get(resid, selector, "background");
            if (!XpShellStyle.ParseRcBmp(sBack, out int id, out var key))
            {
                // a color header (xp's classic shellstyle: background activecaption / buttonface): the header
                // becomes a flat tile of that color, cut like the port's header picture.
                var c = XpShellStyle.ParseColor(sBack);
                if (c == null) return;
                var tile = new ArgbImage(10, 10);
                for (int i = 0; i < tile.Pixels.Length; ++i) tile.Pixels[i] = Premultiplied(Color.FromArgb(255, c.Value));
                PlaceImage(img, part, ImageIdentity.EncodePngVerified(tile), $"{resid} header color {sBack}");
                foreach (var gid in new[] { IDENTIFIER.SIZINGMARGINS, IDENTIFIER.IMAGECOUNT })
                {
                    var gp = Find(part, 0, gid);
                    if (gp == null) continue;
                    object v = gid == IDENTIFIER.IMAGECOUNT ? (object)1 : new Margins(1, 1, 1, 1);
                    if (SemanticModel.FormatValue(gp) == (gid == IDENTIFIER.IMAGECOUNT ? "1" : "(l1,t1,r1,b1)")) continue;
                    gp.SetValue(v);
                    Changed(gp, part, 0, "applied-shellstyle", $"{resid} header is a flat color, a 10x10 tile");
                }
                HeaderInset(part, resid, selector);
                return;
            }
            XpShellStyle.ParseRcBmp(m_base.Get(resid, selector, "background"), out int bid, out _);
            var dib = m_src.GetBitmap(id);
            if (dib == null) { m_imp.Report.Warnings.Add($"shellstyle: {resid} header bitmap {id} is missing; template kept."); return; }
            if (bid > 0 && SameBytes(dib, m_base.GetBitmap(bid))) { Restore(img, part, 0, IDENTIFIER.IMAGEFILE, resid + " header"); RestoreHeaderGeometry(part); HeaderInset(part, resid, selector); return; }
            // mode 6 color keyed; otherwise DirectUI's per-pixel alpha rule.
            PlaceImage(img, part, ImageConverter.ConvertXpDibToPremultipliedPng(dib, key, key == null), $"{resid} header bitmap {id}");
            RestoreHeaderGeometry(part);
            HeaderInset(part, resid, selector);
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
        /// TangoLux's borderthickness 0 makes its headers 2px shorter, as on xp.
        /// </summary>
        private void HeaderInset(int part, string resid, string selector)
        {
            var p = Find(part, 0, IDENTIFIER.CONTENTMARGINS);
            if (p == null || !m_snapshot.TryGetValue(K(part, 0, IDENTIFIER.CONTENTMARGINS), out var orig) || !(orig.GetValue() is Margins t)) return;
            Margins Inset(XpShellStyle s)
            {
                var b = XpShellStyle.ParseRect(s.Get(resid, selector, "borderthickness")) ?? new Margins(0, 0, 0, 0);
                var pd = XpShellStyle.ParseRect(s.Get(resid, selector, "padding")) ?? new Margins(0, 0, 0, 0);
                return new Margins(b.Left + pd.Left, b.Top + pd.Top, b.Right + pd.Right, b.Bottom + pd.Bottom);
            }
            var si = Inset(m_src); var bi = Inset(m_base);
            // only the vertical insets: the port's left margin also places win10's own header icon.
            int top = Math.Max(0, t.Top + si.Top - bi.Top), bottom = Math.Max(0, t.Bottom + si.Bottom - bi.Bottom);
            if (top == t.Top && bottom == t.Bottom) return;
            if (p.GetValue() is Margins cur && cur.Top == top && cur.Bottom == bottom && cur.Left == t.Left && cur.Right == t.Right) return;
            p.SetValue(new Margins(t.Left, top, t.Right, bottom));
            Changed(p, part, 0, "applied-shellstyle", $"{resid} header borderthickness/padding differ from stock luna by {si.Top - bi.Top}px top, {si.Bottom - bi.Bottom}px bottom");
        }

        private void ArrowStrip(int part, string resid, string[] frames)
        {
            var img = Find(part, 0, IDENTIFIER.IMAGEFILE);
            if (img == null) return;
            var src = new List<(byte[] Dib, Color? Key, bool AutoKey)>();
            bool stock = true;
            foreach (var sel in frames)
            {
                if (!XpShellStyle.ParseRcBmp(m_src.Get(resid, sel, "content"), out int id, out var key, out bool autoKey)) return;
                XpShellStyle.ParseRcBmp(m_base.Get(resid, sel, "content"), out int bid, out _);
                var dib = m_src.GetBitmap(id);
                if (dib == null) { m_imp.Report.Warnings.Add($"shellstyle: {resid} arrow bitmap {id} is missing; template kept."); return; }
                stock &= bid > 0 && SameBytes(dib, m_base.GetBitmap(bid));
                src.Add((dib, key, autoKey));
            }
            if (stock)
            {
                Restore(img, part, 0, IDENTIFIER.IMAGEFILE, resid + " arrows");
                var ic = Find(part, 0, IDENTIFIER.IMAGECOUNT);
                if (ic != null) Restore(ic, part, 0, IDENTIFIER.IMAGECOUNT, resid + " arrows");
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
                m_imp.Report.Warnings.Add($"shellstyle: {resid} arrow bitmaps differ in size; template kept.");
                return;
            }
            var strip = new ArgbImage(w, h * pics.Count);
            for (int i = 0; i < pics.Count; ++i) strip.Blit(pics[i], 0, i * h);
            PlaceImage(img, part, ImageIdentity.EncodePngVerified(strip), $"{resid} arrow bitmaps (normal, hot, pressed)");
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
