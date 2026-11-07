using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

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
            // the pack's rules may sit in a sheet the element tree doesn't name (XtremeXP), so the cascade finds nothing.
            string paneSrc = pane.Src ?? m_src.GetAny("element", "atom(sectionlist)", "background");
            string paneStock = pane.Stock ?? (m_base != null ? m_base.GetAny("element", "atom(sectionlist)", "background") : null);
            var sFill = XpShellStyle.ParseFill(paneSrc);
            var bFill = XpShellStyle.ParseFill(paneStock);
            var paneBorder = Both(x => x.Pane, false, "borderthickness");
            string paneBorderSrc = paneBorder.Src ?? m_src.GetAny("element", "atom(sectionlist)", "borderthickness");
            if (sFill == null && XpShellStyle.ParseRcBmp(paneSrc, out _, out _)) PaneBitmap(paneSrc, XpShellStyle.ParseRect(paneBorderSrc));
            else if (Usable(sFill, "task pane background"))
            {
                bool stock = SameFill(sFill, bFill);
                SetColor(Background, 0, IDENTIFIER.GRADIENTCOLOR1, stock ? (Color?)null : sFill[0], "task pane background (start)");
                SetColor(Background, 0, IDENTIFIER.GRADIENTCOLOR2, stock ? (Color?)null : sFill[sFill.Length - 1], "task pane background (end)");
            }

            // xp's folder watermark: the sectionlist's content picture (the theme's mark at the pane's bottom).
            PaneWatermark();

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
            TaskHover(false);
            TaskHover(specialMain);
            SectionIcons();
            GroupBackground();
            ControlPanelPage();
            DetailsText();
            RebarBand();
        }

        // xp's category view (cpview.uix CategoryView, CategoryViewSS) and the win10 record that draws the same thing
        // (SHELL32_UIFILE 24, CategoryPanelStyle: MainContentPanel dtb(CPLHomePage::ControlPanel, 2, 0), MainContentBackground
        // dtb(.., 2, 1) whose glyph is the bottom-right watermark, HomePageTitle gtc(.., 5, 0), category links gtc(.., 11, 0/1/2)).
        private const string CplClass = "CPLHomePage::ControlPanel";
        private const int CplContent = 2, CplTitle = 5, CplLink = 11;
        // shstyle.h IDB_CPANEL_WATERMARK; cpview.cpp _CreateWatermark draws it GRAPHIC_TransColor with blend value 255,
        // RGB(255,0,0): red is see-through, bottom-right aligned.
        private const int CplWatermarkId = 26;

        // xp's details section: shell32's NameSpaceItemInfoList sheet (the info lines' foreground; themed mode has
        // none, so they inherit system windowtext unless a theme overrides it). Windows 10 draws them through
        // genericlayoutstyle's MetadataElement rules, which read PreviewPane: 5/0 the title line, 6/0 the subtitle,
        // 7/0 and 7/1 the metadata rows.
        private const string DetailsSheet = "NameSpaceItemInfoList";
        private const string PreviewClass = "PreviewPane";
        private static readonly (int Part, int State)[] DetailsParts = { (5, 0), (6, 0), (7, 0), (7, 1) };

        /// <summary>
        /// The Control Panel's "Pick a category" page. xp drew it from the shellstyle (CategoryViewSS view background,
        /// DirectiveSS "Pick a category" title, CategoryListItemSS links, IDB_CPANEL_WATERMARK); Windows 10 draws it from
        /// CPLHomePage::ControlPanel. Each value the source changed from stock xp goes into that record. The content
        /// background is a picture in Luna10 (a 2px wide blue strip under the watermark glyph), so a changed fill becomes
        /// a flat strip of the source's color, and a changed watermark replaces the glyph.
        /// </summary>
        private void ControlPanelPage()
        {
            if (!m_imp.Target.ClassesByName.TryGetValue(CplClass, out var cls) || m_imp.Target.DuplicateClassNames.Contains(CplClass)) return;
            string S(XpShellStyle st, string sheet, string sel, string prop) => st.Get(sheet, sel, prop);
            Color? Src(string sheet, string sel, string prop, out bool stock)
            {
                var s = XpShellStyle.ParseColor(S(m_src, sheet, sel, prop));
                var b = XpShellStyle.ParseColor(S(m_base, sheet, sel, prop));
                stock = s == null || SameColor(s, b);
                return s;
            }

            var bg = Src("CategoryViewSS", "element [id=atom(view)]", "background", out bool bgStock);
            if (!bgStock && bg is Color fill)
            {
                ClassColor(cls, CplContent, 0, IDENTIFIER.FILLCOLOR, fill, "Control Panel category view's background");
                CplContentStrip(cls, fill);
            }
            var title = Src("DirectiveSS", "element", "foreground", out bool titleStock);
            if (!titleStock && title is Color tc) ClassColor(cls, CplTitle, 0, IDENTIFIER.TEXTCOLOR, tc, "Control Panel category view's \"Pick a category\" title color");
            var link = Src("CategoryListItemSS", "linkelement", "foreground", out bool linkStock);
            if (!linkStock && link is Color lc)
                foreach (int state in new[] { 0, 1, 2 }) ClassColor(cls, CplLink, state, IDENTIFIER.TEXTCOLOR, lc, "Control Panel category view's link color");
            CplWatermark(cls);
        }

        private StyleState ClassState(StyleClass cls, int part, int state)
        {
            if (!cls.Parts.TryGetValue(part, out var p)) return null;
            if (!p.States.TryGetValue(state, out var st))
            {
                st = new StyleState { StateId = state, StateName = "State " + state };
                p.States.Add(state, st);
            }
            return st;
        }

        private StyleProperty ClassProp(StyleClass cls, int part, StyleState st, IDENTIFIER name, IDENTIFIER type)
        {
            var prop = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)name);
            if (prop != null) return prop;
            prop = new StyleProperty(new PropertyHeader((int)name, (int)type) { classID = cls.ClassId, partID = part, stateID = st.StateId });
            st.Properties.Add(prop);
            m_imp.AddedRecords.Add($"{cls.ClassId}/{part}/{st.StateId}/{(int)name}/{(int)type}");
            return prop;
        }

        private void ClassChanged(StyleClass cls, StyleProperty p, int part, int state, string reason)
        {
            string partName = SemanticModel.PartNames(cls.ClassName, Platform.Win10).TryGetValue(part, out var n) ? n : "Part " + part;
            string prop = SemanticModel.PropertyName(p.Header.nameID);
            m_imp.MarkTouched(p, $"{cls.ClassName}|{partName}|S{state}|{prop}");
            m_imp.Report.Decisions.Add(new MergeDecision
            {
                Class = cls.ClassName, Part = partName, State = state, Property = prop, Action = "applied-shellstyle",
                Reason = $"{reason} from the source shellstyle", Result = SemanticModel.FormatValue(p),
            });
            m_imp.Report.Count("applied-shellstyle");
        }

        private void ClassColor(StyleClass cls, int part, int state, IDENTIFIER name, Color c, string reason)
        {
            var st = ClassState(cls, part, state);
            if (st == null) return;
            var p = ClassProp(cls, part, st, name, IDENTIFIER.COLOR);
            var v = Color.FromArgb(255, c);
            if (p.GetValue() is Color cur && cur.ToArgb() == v.ToArgb()) return;
            p.SetValue(v);
            ClassChanged(cls, p, part, state, reason);
        }

        /// <summary>the content background picture (state 1): Luna10's blue strip, redrawn flat in the source's color, template size.</summary>
        private void CplContentStrip(StyleClass cls, Color fill)
        {
            var st = ClassState(cls, CplContent, 1);
            var img = st?.Properties.FirstOrDefault(x => x.Header.nameID == (int)IDENTIFIER.IMAGEFILE);
            if (img == null || !(img.GetValue() is int id)) return;
            var t = m_imp.Decoded(m_imp.Target, "IMAGE", id);
            int w = t?.Width ?? 2, h = t?.Height ?? 300;
            var strip = new ArgbImage(w, h);
            uint px = 0xFF000000u | ((uint)fill.R << 16) | ((uint)fill.G << 8) | fill.B;
            for (int i = 0; i < strip.Pixels.Length; ++i) strip.Pixels[i] = px;
            byte[] png = ImageIdentity.EncodePngVerified(strip);
            int nid = m_imp.PlaceResource("IMAGE", -1, png, ImageIdentity.Analyze(png));
            if (nid == id) return;
            img.SetValue(nid);
            ClassChanged(cls, img, CplContent, 1, "Control Panel category view background (flat strip)");
        }

        /// <summary>the watermark: glyph of the content background (state 1), bottom-right like xp's.</summary>
        private void CplWatermark(StyleClass cls)
        {
            var src = m_src.GetBitmap(CplWatermarkId);
            if (src == null || SameBytes(src, m_base.GetBitmap(CplWatermarkId))) return;
            var st = ClassState(cls, CplContent, 1);
            var glyph = st?.Properties.FirstOrDefault(x => x.Header.nameID == (int)IDENTIFIER.GLYPHIMAGEFILE);
            if (glyph == null) { m_imp.Report.Warnings.Add($"shellstyle: the template's {CplClass} content background has no glyph; the Control Panel watermark stays."); return; }
            byte[] png = ImageConverter.ConvertXpDibToPremultipliedPng(src, Color.FromArgb(255, 0, 0));
            int id = m_imp.PlaceResource("IMAGE", -1, png, ImageIdentity.Analyze(png));
            if ((glyph.Header.shortFlag & 0xFFFF) == id) return;
            glyph.SetValue(id);
            ClassChanged(cls, glyph, CplContent, 1, "Control Panel category view watermark (bitmap 26)");
        }

        /// <summary>
        /// The details section's text ("My Computer" / "System Folder" under the Details header): xp's info lines draw
        /// no color of their own in themed mode and inherit system windowtext, and a theme overrides it in its shellstyle
        /// (<see cref="DetailsSheet"/>: CrystalBlue's `foreground: white` over its dark group art; stock luna's sheet has
        /// none). Windows 10's genericlayoutstyle reads <see cref="PreviewClass"/> instead, so the source's override
        /// becomes those records; without one the template's black stays, as on xp.
        /// </summary>
        /// <summary>
        /// The rebar band the address bar sits on. XP draws Rebar|BAND state 0 as a see-through band (TRANSPARENT true, no
        /// fill of its own), so the rebar background picture (Rebar part 0 IMAGEFILE, XtremeXP's ribbed strip) shows
        /// through it. Windows 10 fills the band's own box from its BGTYPE 2 fill instead, leaving a flat strip where XP
        /// had the picture, so the band gets the rebar background's picture and geometry.
        /// </summary>
        private void RebarBand()
        {
            const string rebar = "Rebar";
            if (!m_imp.Target.ClassesByName.TryGetValue(rebar, out var cls) || m_imp.Target.DuplicateClassNames.Contains(rebar)) return;
            if (!cls.Parts.TryGetValue(0, out var bgPart) || !bgPart.States.TryGetValue(0, out var bgSt)) return;
            if (!cls.Parts.TryGetValue(3, out var bandPart) || !bandPart.States.TryGetValue(0, out var bandSt)) return;
            if (!(bgSt.Properties.FirstOrDefault(x => x.Header.nameID == (int)IDENTIFIER.IMAGEFILE)?.GetValue() is int imageId)) return;

            StyleProperty P(StyleState st, IDENTIFIER name) => st.Properties.FirstOrDefault(x => x.Header.nameID == (int)name);
            bool seeThrough;
            {
                object tr = P(bandSt, IDENTIFIER.TRANSPARENT_)?.GetValue();
                seeThrough = tr is bool tb ? tb : tr is int ti && ti != 0;
            }
            if (!seeThrough) return;
            if (P(bandSt, IDENTIFIER.FILLCOLOR) != null || P(bandSt, IDENTIFIER.FILLCOLORHINT) != null) return;
            if (!(P(bandSt, IDENTIFIER.BGTYPE)?.GetValue() is int bt && bt == 2)) return;

            void Copy(IDENTIFIER name, object value, IDENTIFIER type)
            {
                var dst = ClassProp(cls, 3, bandSt, name, type);
                if (value is Margins mg) dst.SetValue(new Margins(mg.Left, mg.Top, mg.Right, mg.Bottom));
                else if (value is Color c) dst.SetValue(Color.FromArgb(255, c));
                else if (value is int i) dst.SetValue(i);
                else dst.SetValue(value);
                ClassChanged(cls, dst, 3, 0, "address bar band background (xp's see-through band shows the rebar picture through it)");
            }
            Copy(IDENTIFIER.BGTYPE, 0, IDENTIFIER.ENUM);
            Copy(IDENTIFIER.IMAGEFILE, imageId, IDENTIFIER.FILENAME);
            Copy(IDENTIFIER.IMAGECOUNT, 1, IDENTIFIER.INT);
            foreach (var name in new[] { IDENTIFIER.IMAGELAYOUT, IDENTIFIER.SIZINGTYPE, IDENTIFIER.SIZINGMARGINS, IDENTIFIER.TRANSPARENT_, IDENTIFIER.TRANSPARENTCOLOR })
            {
                var src = P(bgSt, name);
                if (src == null) continue;
                Copy(name, src.GetValue(), (IDENTIFIER)src.Header.typeID);
            }
        }

        private void DetailsText()
        {
            if (!m_imp.Target.ClassesByName.TryGetValue(PreviewClass, out var cls) || m_imp.Target.DuplicateClassNames.Contains(PreviewClass)) return;
            var c = XpShellStyle.ParseColor(m_src.Get(DetailsSheet, "NameSpaceItemInfo", "foreground"));
            if (c == null) return;
            if (SameColor(c, XpShellStyle.ParseColor(m_base.Get(DetailsSheet, "NameSpaceItemInfo", "foreground")))) return;
            foreach (var (part, state) in DetailsParts)
                ClassColor(cls, part, state, IDENTIFIER.TEXTCOLOR, c.Value, "details section text (NameSpaceItemInfo foreground)");
        }

        // xp's special section icons (shstyle.h IDB_MUSIC_ICON_BMP 10, IDB_PICTURES_ICON_BMP 13, IDB_CPANEL_ICON_BMP 22) in
        // the order of the frames of the win10 picture that replaced them: CommandModule part 12, drawn by the Music Tasks,
        // Picture Tasks and Control Panel headers as dtb(CommandModule, 12, 1/2/3) (SHELL32_UIFILE 4, 5, 18).
        private static readonly int[] SectionIconIds = { 10, 13, 22 };
        private const string IconClass = "CommandModule";
        private const int IconPart = 12;

        /// <summary>whether the source shellstyle has any of the special-section icon bitmaps (10/13/22). When it
        /// hasn't (Glitch), xp drew no icon next to "Picture Tasks"/"Music Tasks" and the template's strip must go too.</summary>
        public bool HasSectionIcons { get; private set; }

        /// <summary>
        /// The special section icons (the music note on Music Tasks). xp drew the shellstyle's bitmap
        /// (duiview.cpp _BuildSection: DUILoadBitmap, GRAPHIC_AlphaConstPerPix, a 32bpp alpha picture); Windows 10
        /// draws CommandModule part 12 instead, one frame per section, and never reads the shellstyle bitmap. Where
        /// the source changed any of them, the strip is rebuilt from the source's icons (stock xp's where it kept one)
        /// in the template strip's frame order and layout. Luna10's frames are stock xp's pictures, so a source that
        /// keeps all three leaves the template's strip.
        /// </summary>
        private void SectionIcons()
        {
            if (!m_imp.Target.ClassesByName.TryGetValue(IconClass, out var cls) || !cls.Parts.TryGetValue(IconPart, out var part)
                || !part.States.TryGetValue(0, out var st)) return;
            var src = SectionIconIds.Select(b => m_src.GetBitmap(b)).ToArray();
            var stock = SectionIconIds.Select(b => m_base.GetBitmap(b)).ToArray();
            HasSectionIcons = src.Any(b => b != null);
            if (src.All(b => b == null) || src.Zip(stock, (s, b) => s == null || SameBytes(s, b)).All(x => x)) return;
            var img = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)IDENTIFIER.IMAGEFILE);
            var count = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)IDENTIFIER.IMAGECOUNT);
            var layout = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)IDENTIFIER.IMAGELAYOUT);
            if (img == null || !(count?.GetValue() is int n) || n != SectionIconIds.Length) { m_imp.Report.Warnings.Add($"shellstyle: the template's {IconClass} part {IconPart} isn't a {SectionIconIds.Length}-frame picture; section icons stay."); return; }
            var pics = src.Select((b, i) => b ?? stock[i]).Select(b => b == null ? null
                : ImageIdentity.Decode(ImageConverter.ConvertXpDibToPremultipliedPng(b, null, anyAlpha: true))).ToList();
            if (pics.Any(p => p == null)) { m_imp.Report.Warnings.Add("shellstyle: a section icon is missing from both the source and stock shellstyle; section icons stay."); return; }
            int w = pics[0].Width, h = pics[0].Height;
            if (pics.Any(p => p.Width != w || p.Height != h)) { m_imp.Report.Warnings.Add("shellstyle: the source's section icons differ in size; section icons stay."); return; }
            bool vertical = layout?.GetValue() is int lo && lo == 0;
            var strip = vertical ? new ArgbImage(w, h * pics.Count) : new ArgbImage(w * pics.Count, h);
            for (int i = 0; i < pics.Count; ++i) strip.Blit(pics[i], vertical ? 0 : i * w, vertical ? i * h : 0);
            byte[] png = ImageIdentity.EncodePngVerified(strip);
            int id = m_imp.PlaceResource("IMAGE", -1, png, ImageIdentity.Analyze(png));
            if ((img.Header.shortFlag & 0xFFFF) == id) return;
            img.SetValue(id);
            m_imp.MarkTouched(img, $"{IconClass}|Part {IconPart}|S0|IMAGEFILE");
            m_imp.Report.Decisions.Add(new MergeDecision
            {
                Class = IconClass, Part = "Part " + IconPart, State = 0, Property = "IMAGEFILE", Action = "applied-shellstyle",
                Reason = "the music, picture and control panel section icons of the source shellstyle (bitmaps 10, 13, 22; win10 draws them from this strip)",
                Result = "IMAGE " + id,
            });
            m_imp.Report.Count("applied-shellstyle");
        }

        // xp's special section background pictures (shstyle.h IDB_MUSIC_TASKS_BMP 11, IDB_PICTURES_TASKS_BMP 14,
        // IDB_VIDEO_TASKS_BMP 20). Windows 10 draws the group's background from CommandModule part 11, one frame per
        // section: the template's rules read dtb(CommandModule, 11, 1) for music_bg and (11, 2) for photo_bg, the
        // same shape the working Luna port uses. Frame 0 stays transparent (no art for other groups).
        private static readonly int[] GroupBackgroundIds = { 11, 14 };
        private const int GroupPart = 11;

        /// <summary>
        /// the music/pictures section backgrounds: rebuilds CommandModule part 11's strip from the source shellstyle
        /// (frames 1 and 2; frame 0 transparent, like xp's plain groups). Stock xp art keeps the template strip.
        /// </summary>
        private void GroupBackground()
        {
            if (!m_imp.Target.ClassesByName.TryGetValue(IconClass, out var cls) || !cls.Parts.TryGetValue(GroupPart, out var part)
                || !part.States.TryGetValue(0, out var st)) return;
            var src = GroupBackgroundIds.Select(b => m_src.GetBitmap(b)).ToArray();
            var stock = GroupBackgroundIds.Select(b => m_base.GetBitmap(b)).ToArray();
            if (src.All(b => b == null) || src.Zip(stock, (s, b) => s == null || SameBytes(s, b)).All(x => x)) return;
            var img = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)IDENTIFIER.IMAGEFILE);
            var count = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)IDENTIFIER.IMAGECOUNT);
            var layout = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)IDENTIFIER.IMAGELAYOUT);
            if (img == null || !(count?.GetValue() is int n) || n != GroupBackgroundIds.Length + 1)
            { m_imp.Report.Warnings.Add($"shellstyle: the template's {IconClass} part {GroupPart} isn't a {GroupBackgroundIds.Length + 1}-frame picture; the section backgrounds stay."); return; }
            // keyed like xp's group picture: the art's own background colour turns transparent, the note stays
            var pics = src.Select((b, i) => b ?? stock[i]).Select(b => b == null ? null
                : ImageIdentity.Decode(XpShellBitmap(b, null, true))).ToList();
            if (pics.Any(p => p == null)) { m_imp.Report.Warnings.Add("shellstyle: a section background is missing from both the source and stock shellstyle; the section backgrounds stay."); return; }
            int w = pics.Max(p => p.Width), h = pics.Max(p => p.Height);
            bool vertical = layout?.GetValue() is int lo && lo == 0;
            var strip = vertical ? new ArgbImage(w, h * (pics.Count + 1)) : new ArgbImage(w * (pics.Count + 1), h);
            for (int i = 0; i < pics.Count; ++i) strip.Blit(pics[i], vertical ? 0 : (i + 1) * w, vertical ? (i + 1) * h : 0);
            byte[] png = ImageIdentity.EncodePngVerified(strip);
            int id = m_imp.PlaceResource("IMAGE", -1, png, ImageIdentity.Analyze(png));
            if ((img.Header.shortFlag & 0xFFFF) == id) return;
            img.SetValue(id);
            m_imp.MarkTouched(img, $"{IconClass}|Part {GroupPart}|S0|IMAGEFILE");
            m_imp.Report.Decisions.Add(new MergeDecision
            {
                Class = IconClass, Part = "Part " + GroupPart, State = 0, Property = "IMAGEFILE", Action = "applied-shellstyle",
                Reason = "the music and picture section backgrounds of the source shellstyle (bitmaps 11, 14; win10 draws them from this strip, frame 1/2 via dtb(CommandModule, 11, 1/2))",
                Result = "IMAGE " + id,
            });
            m_imp.Report.Count("applied-shellstyle");
        }

        /// <summary>
        /// state of the group background part (normal 5, special 9) that carries the task hover picture, per section
        /// (true = main/special). Only set where the source draws one; <see cref="PatchShellStyleLayout"/> points the
        /// task buttons' hover rules at it.
        /// </summary>
        public Dictionary<bool, (int Part, int State)> HoverRecords { get; } = new Dictionary<bool, (int Part, int State)>();

        /// <summary>
        /// ExplorerBar record holding the source's task pane background picture; null where the source's pane is a
        /// color or gradient. <see cref="PatchShellStyleLayout"/> points the pane's background at it.
        /// </summary>
        public (int Part, int State)? PaneRecord { get; private set; }

        /// <summary>one watermark picture's ExplorerBar record and its height in pixels.</summary>
        public readonly struct PaneWatermarkRecord
        {
            public PaneWatermarkRecord(int part, int state, int height) { Part = part; State = state; Height = height; }
            public int Part { get; }
            public int State { get; }
            public int Height { get; }
        }

        /// <summary>
        /// The source's pane watermarks. <c>All</c> is the taskpane sectionlist's content picture (drawn on every
        /// folder's pane); <c>Music</c>/<c>Pictures</c> are xp's per-folder pictures (shellstyle bitmaps 12 and 15, IDB_*_LISTVIEW_BMP,
        /// drawn only on music/picture folders' panes). <see cref="PatchShellStyleLayout"/> adds a bottom band per layout.
        /// </summary>
        public class PaneWatermarkSet
        {
            public PaneWatermarkRecord? All { get; set; }
            public PaneWatermarkRecord? Music { get; set; }
            public PaneWatermarkRecord? Pictures { get; set; }
        }

        public PaneWatermarkSet Watermarks { get; } = new PaneWatermarkSet();

        /// <summary>
        /// xp's folder watermark: the taskpane sectionlist's content picture (CrystalBlue: rcbmp(113, 2, 255), the "(c)
        /// 2006 beginner design" line in white, contentalign bottomcenter, bottom padding 1rp). Windows 10's folder
        /// layout has no such element and draws no shellstyle rcbmp, so the picture goes into a spare state of the
        /// background part and the layout patch adds a bottom band that draws it.
        /// </summary>
        // xp drew two kinds of pane watermark: the taskpane sectionlist's own content picture (every folder's pane) and
        // per-folder pictures from the shellstyle, chosen by shell32's folder template (music 12, pictures 15,
        // search 18, video 21; nt5src shell\inc\shstyle.h IDB_*_LISTVIEW_BMP). Windows 10's DirectUI draws no shellstyle
        // rcbmp, so each picture goes into its own spare state of ExplorerBar part 0, and only the matching folder
        // layout's pane gets the bottom band that draws it (a plain folder shows none, like xp).
        private const int MusicWatermarkId = 12;
        private const int PicturesWatermarkId = 15;
        // states of part 0 the shell doesn't read: its UIFILE draws state 0 (gradient) and state 1 (pane picture).
        private const int FirstWatermarkState = 2;
        private int m_nextWatermarkState = FirstWatermarkState;
        private const string WatermarkReason = "folder watermark of the source shellstyle (win10 draws it from this record, see the shellstyle layout notes)";

        private void PaneWatermark()
        {
            // the sectionlist content picture: a theme-wide watermark (CrystalBlue: rcbmp(113, 2, 255), "(c) 2006
            // beginner design"), drawn on every pane. Glitch's rule is a transparent 1x1, so nothing is recorded for it
            // unless it differs from stock.
            var water = Both(x => x.Pane, false, "content");
            if (XpShellStyle.ParseRcBmp(water.Src, out int wid, out Color? wkey, out bool wAuto))
            {
                byte[] dib = m_src.GetBitmap(wid);
                if (dib != null)
                {
                    var png = XpShellBitmap(dib, wkey, wAuto);
                    bool sameAsStock = XpShellStyle.ParseRcBmp(water.Stock, out int sid, out var skey, out bool sAuto)
                        && (m_base.GetBitmap(sid) is byte[] sdib) && SameBytes(png, XpShellBitmap(sdib, skey, sAuto));
                    if (!sameAsStock) Watermarks.All = AddWatermark(png, "task pane watermark");
                }
            }
            // the per-folder pictures: only music and picture folders have pane layouts in the win10 templates.
            Watermarks.Music = FolderWatermark(MusicWatermarkId, "music folder watermark");
            Watermarks.Pictures = FolderWatermark(PicturesWatermarkId, "picture folder watermark");
        }

        /// <summary>a per-folder watermark picture (shellstyle bitmap), as a record; null where it's missing or stock.</summary>
        private PaneWatermarkRecord? FolderWatermark(int id, string reason)
        {
            byte[] dib = m_src.GetBitmap(id);
            if (dib == null) return null;
            // xp's template drew these with a color key (the picture is white-backed art on the blue pane)
            var png = XpShellBitmap(dib, Color.White, false);
            byte[] stock = m_base.GetBitmap(id);
            if (stock != null && SameBytes(png, XpShellBitmap(stock, Color.White, false))) return null;
            // fills the strip under the section's rows (the record's SIZINGTYPE is STRETCH)
            return AddWatermark(png, reason, 1);
        }

        /// <summary>stores a watermark picture in the next spare state of the background part.</summary>
        private PaneWatermarkRecord? AddWatermark(byte[] png, string reason, int sizingType = 1)
        {
            if (!m_cls.Parts.TryGetValue(Background, out var p)) return null;
            int state = m_nextWatermarkState++;
            if (!p.States.TryGetValue(state, out var st))
            {
                st = new StyleState { StateId = state, StateName = "State " + state };
                p.States.Add(state, st);
            }
            int img = m_imp.PlaceResource("IMAGE", -1, png, ImageIdentity.Analyze(png));
            Put(st, Background, IDENTIFIER.BGTYPE, IDENTIFIER.ENUM, 0, reason, WatermarkReason);       // IMAGEFILE
            Put(st, Background, IDENTIFIER.IMAGEFILE, IDENTIFIER.FILENAME, img, reason, WatermarkReason);
            Put(st, Background, IDENTIFIER.IMAGECOUNT, IDENTIFIER.INT, 1, reason, WatermarkReason);
            Put(st, Background, IDENTIFIER.SIZINGTYPE, IDENTIFIER.ENUM, sizingType, reason, WatermarkReason);  // 0 TRUESIZE, 1 STRETCH
            Put(st, Background, IDENTIFIER.SIZINGMARGINS, IDENTIFIER.MARGINS, new Margins(0, 0, 0, 0), reason, WatermarkReason);
            return new PaneWatermarkRecord(Background, state, ImageIdentity.Decode(png).Height);
        }


        /// <summary>an XP shellstyle bitmap as the png win10 stores: the color key (or its automatic top-left pixel)
        /// see-through, per-pixel alpha where the DirectUI mode has none (mode 2).</summary>
        private byte[] XpShellBitmap(byte[] dib, Color? key, bool autoKey)
        {
            if (autoKey)
            {
                uint p0 = ImageIdentity.Decode(ImageConverter.ConvertXpDibToPremultipliedPng(dib, null)).Pixels[0];
                key = Color.FromArgb((int)((p0 >> 16) & 0xFF), (int)((p0 >> 8) & 0xFF), (int)(p0 & 0xFF));
            }
            return ImageConverter.ConvertXpDibToPremultipliedPng(dib, key, key == null);
        }

        /// <summary>
        /// xp's task pane background as a bitmap (Ana Muslims Vista: rcbmp(120, 5, ...), a 200x604 grey-to-white
        /// picture on the sectionlist). Windows 10 fills the pane from gradient(gtc(ExplorerBar, 0, 0, 3810/3811)) and its
        /// DirectUI doesn't draw a shellstyle's rcbmp (see <see cref="TaskHover"/>), so the picture goes into a spare
        /// state of the background part and the pane draws it with dtb(). On xp it lay over the sectionlist, which is only
        /// as tall as the sections in it (folder.uix: a verticalflowlayout in a ScrollViewer). Windows 10's
        /// ExplorerBarBackground fills the pane's whole height instead, and any stretch or nine-grid there scales the
        /// picture with the window (a nine-grid squeezes once the pane is shorter than its margins). So the picture is
        /// drawn unscaled from the top-left: SIZINGTYPE TILE, which clips rather than scales, on a copy padded with its
        /// own last column and row out to <see cref="PaneTileWidth"/> x <see cref="PaneTileHeight"/>, so a wider or
        /// taller pane continues the picture's edge colors instead of repeating it.
        /// </summary>
        /// <summary>a picture without its left and right border columns (the band spans the window, so those would
        /// draw as vertical strips at its edges).</summary>
        private static ArgbImage CropColumns(ArgbImage pic, int left, int right)
        {
            int l = Math.Max(0, Math.Min(left, pic.Width / 2 - 1));
            int r = Math.Max(0, Math.Min(right, pic.Width - l - 1));
            int w = pic.Width - l - r;
            var dst = new ArgbImage(w, pic.Height);
            for (int y = 0; y < pic.Height; ++y)
                for (int x = 0; x < w; ++x)
                    dst.Pixels[y * w + x] = pic.Pixels[y * pic.Width + (x + l)];
            return dst;
        }

        /// <summary>draws <paramref name="pic"/> nine-sliced into <paramref name="dst"/>: the border pixels keep their
        /// size, the middle stretches. The margins are clamped to the picture so a pack with oversized margins still
        /// renders.</summary>
        private static void NineSlice(ArgbImage pic, Margins m, ArgbImage dst)
        {
            int l = Math.Max(0, Math.Min(m.Left, pic.Width - 1)), r = Math.Max(0, Math.Min(m.Right, pic.Width - 1 - l));
            int t = Math.Max(0, Math.Min(m.Top, pic.Height - 1)), b = Math.Max(0, Math.Min(m.Bottom, pic.Height - 1 - t));
            int dl = Math.Min(l, dst.Width - 1), dr = Math.Min(r, dst.Width - 1 - dl);
            int dt = Math.Min(t, dst.Height - 1), db = Math.Min(b, dst.Height - 1 - dt);
            for (int y = 0; y < dst.Height; ++y)
            {
                int sy;
                if (y < dt) sy = y;
                else if (y >= dst.Height - db) sy = pic.Height - (dst.Height - y);
                else sy = t + (int)((long)(y - dt) * (pic.Height - t - b) / Math.Max(1, dst.Height - dt - db));
                sy = Math.Max(0, Math.Min(sy, pic.Height - 1));
                for (int x = 0; x < dst.Width; ++x)
                {
                    int sx;
                    if (x < dl) sx = x;
                    else if (x >= dst.Width - dr) sx = pic.Width - (dst.Width - x);
                    else sx = l + (int)((long)(x - dl) * (pic.Width - l - r) / Math.Max(1, dst.Width - dl - dr));
                    sx = Math.Max(0, Math.Min(sx, pic.Width - 1));
                    dst.Pixels[y * dst.Width + x] = pic.Pixels[sy * pic.Width + sx];
                }
            }
        }

        private void PaneBitmap(string v, Margins thick)
        {
            XpShellStyle.ParseRcBmp(v, out int id, out var key, out bool autoKey);
            var dib = m_src.GetBitmap(id);
            if (dib == null) { m_imp.Report.Warnings.Add($"shellstyle: task pane background bitmap {id} is missing; template kept."); return; }
            if (autoKey)
            {
                uint p0 = ImageIdentity.Decode(ImageConverter.ConvertXpDibToPremultipliedPng(dib, null)).Pixels[0];
                key = Color.FromArgb((int)((p0 >> 16) & 0xFF), (int)((p0 >> 8) & 0xFF), (int)(p0 & 0xFF));
            }
            // nine-grid pictures (rcbmp mode 5/6/7) are a frame: their borderthickness says which edges stay put and
            // which middle stretches, so they go in whole with those margins (stretch + SIZINGMARGINS is a nine-grid in
            // the theme records). The padding below would leave the frame's top-left corner in place and continue its
            // last row down the pane (Glitch's 159x83 black-body frame showed a grey strip instead of the body).
            int? mode = XpShellStyle.RcBmpMode(v);
            bool nineGrid = mode.HasValue && mode.Value >= 5 && mode.Value <= 7 && thick != null
                && (thick.Left | thick.Top | thick.Right | thick.Bottom) != 0;
            byte[] png;
            Margins sizing;
            int sizingType;
            var pic = ImageIdentity.Decode(ImageConverter.ConvertXpDibToPremultipliedPng(dib, key, key == null));
            int w = Math.Max(pic.Width, PaneTileWidth), h = Math.Max(pic.Height, PaneTileHeight);
            var padded = new ArgbImage(w, h);
            if (nineGrid)
            {
                // Explorer's pane renderer stretches the ExplorerBar picture across the whole pane and ignores the
                // record's SIZINGMARGINS (tried live: the frame's top band scaled with the pane), so the nine-grid is
                // rendered into the tile here: borders keep their pixels, the middle stretches.
                // the band spans the window: the frame's left/right border columns would show as light vertical
                // strips at its edges, so they are cropped away and the gradient between them spans the band's width.
                NineSlice(CropColumns(pic, thick.Left, thick.Right), new Margins(0, thick.Top, 0, thick.Bottom), padded);
                // STRETCH, not TILE: in the band layout the pane is a full-width strip on top, so the picture has to
                // scale across it (a tile would repeat the frame's left/right borders every 210px).
                sizing = new Margins(0, 0, 0, 0);
                sizingType = 1; // STRETCH
            }
            else
            {
                for (int y = 0; y < h; ++y)
                    for (int x = 0; x < w; ++x)
                        padded.Pixels[y * w + x] = pic.Pixels[Math.Min(y, pic.Height - 1) * pic.Width + Math.Min(x, pic.Width - 1)];
                sizing = new Margins(0, 0, 0, 0);
                sizingType = 2; // TILE
            }
            png = ImageIdentity.EncodePngVerified(padded);
            if (!m_cls.Parts.TryGetValue(Background, out var p)) return;
            if (!p.States.TryGetValue(PaneState, out var st))
            {
                st = new StyleState { StateId = PaneState, StateName = "State " + PaneState };
                p.States.Add(PaneState, st);
            }
            const string what = "task pane background";
            int img = m_imp.PlaceResource("IMAGE", -1, png, ImageIdentity.Analyze(png));
            Put(st, Background, IDENTIFIER.BGTYPE, IDENTIFIER.ENUM, 0, what, PaneReason);              // IMAGEFILE
            Put(st, Background, IDENTIFIER.IMAGEFILE, IDENTIFIER.FILENAME, img, what, PaneReason);
            Put(st, Background, IDENTIFIER.IMAGECOUNT, IDENTIFIER.INT, 1, what, PaneReason);
            Put(st, Background, IDENTIFIER.SIZINGTYPE, IDENTIFIER.ENUM, sizingType, what, PaneReason);
            Put(st, Background, IDENTIFIER.SIZINGMARGINS, IDENTIFIER.MARGINS, sizing, what, PaneReason);
            PaneRecord = (Background, PaneState);
        }

        // a state of the background part nothing in the shell draws (the UIFILEs read it through gtc state 0 only).
        private const int PaneState = 1;
        // the padded pane picture: the 210rp pane at 100%, 1200px tall. Applying a style with a 420x2160 one failed
        // (SetSystemVisualStyle 0x8007000E, out of memory); a taller pane past this repeats the picture.
        private const int PaneTileWidth = 210, PaneTileHeight = 1200;
        private const string PaneReason = "bitmap of the source shellstyle (win10 draws it from this record, see the shellstyle layout notes)";
        private const string HoverReason = "task hover background of the source shellstyle (win10 draws it from this record, see the shellstyle layout notes)";

        // a state of the group background part nothing in the shell draws (the UIFILEs only read state 0).
        private const int HoverState = 1;

        /// <summary>
        /// xp's task hover background: the task button's background under the mouse (Lion OS X: rcbmp(110, 6, magenta)
        /// in sectiontaskss/mainsectiontaskss <c>button [mousefocused]</c>). Windows 10 gives task links no hover
        /// background of its own, and its DirectUI doesn't draw a shellstyle's rcbmp/resbmp (tried live: nothing
        /// shows), only theme records through dtb(). So the picture goes into the port as a record on a spare state of
        /// the group background part; mode 6 is a nine-grid stretched with the button's borderthickness as margins
        /// (render.cpp GRAPHIC_NineGridTransColor), the key color see-through. Stock luna has no hover background, so
        /// sources without one change nothing.
        /// </summary>
        private void TaskHover(bool main)
        {
            var hot = DuiCascade.Build(main, hot: true); var cold = DuiCascade.Build(main);
            string sHot = m_srcDui.Resolve(hot.TaskButton, "background"), sCold = m_srcDui.Resolve(cold.TaskButton, "background");
            if (sHot == null || sHot == sCold) return;
            string what = main ? "special group" : "normal group";
            int part = main ? SpecialBg : NormalBg;
            var thick = XpShellStyle.ParseRect(m_srcDui.Resolve(hot.TaskButton, "borderthickness")) ?? new Margins(0, 0, 0, 0);
            byte[] png; Margins sizing;
            if (XpShellStyle.ParseRcBmp(sHot, out int id, out var key))
            {
                var dib = m_src.GetBitmap(id);
                if (dib == null) { m_imp.Report.Warnings.Add($"shellstyle: {what} task hover bitmap {id} is missing; no hover background."); return; }
                png = ImageConverter.ConvertXpDibToPremultipliedPng(dib, key, key == null);
                sizing = thick;
            }
            else if (DuiFill.Parse(sHot) is DuiFill f && f.Opaque)
            {
                var border = XpShellStyle.ParseColor(m_srcDui.Resolve(hot.TaskButton, "bordercolor")) ?? Color.Black;
                png = ImageIdentity.EncodePngVerified(Paint(f, border, thick, out var gs));
                sizing = gs ?? new Margins(Math.Min(thick.Left, 4), Math.Min(thick.Top, 4), Math.Min(thick.Right, 4), Math.Min(thick.Bottom, 4));
            }
            else { m_imp.Report.Warnings.Add($"shellstyle: {what} task hover background \"{sHot}\" isn't a bitmap, color or gradient the porter reads; no hover background."); return; }

            if (!m_cls.Parts.TryGetValue(part, out var p)) return;
            if (!p.States.TryGetValue(HoverState, out var st))
            {
                st = new StyleState { StateId = HoverState, StateName = "State " + HoverState };
                p.States.Add(HoverState, st);
            }
            int img = m_imp.PlaceResource("IMAGE", -1, png, ImageIdentity.Analyze(png));
            Put(st, part, IDENTIFIER.BGTYPE, IDENTIFIER.ENUM, 0, what, HoverReason);              // IMAGEFILE
            Put(st, part, IDENTIFIER.IMAGEFILE, IDENTIFIER.FILENAME, img, what, HoverReason);
            Put(st, part, IDENTIFIER.IMAGECOUNT, IDENTIFIER.INT, 1, what, HoverReason);
            Put(st, part, IDENTIFIER.SIZINGTYPE, IDENTIFIER.ENUM, 1, what, HoverReason);          // STRETCH
            Put(st, part, IDENTIFIER.SIZINGMARGINS, IDENTIFIER.MARGINS, sizing, what, HoverReason);
            HoverRecords[main] = (part, HoverState);
        }

        private void Put(StyleState st, int part, IDENTIFIER name, IDENTIFIER type, object value, string what, string reason)
        {
            var prop = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)name);
            if (prop == null)
            {
                prop = new StyleProperty(new PropertyHeader((int)name, (int)type) { classID = m_cls.ClassId, partID = part, stateID = st.StateId });
                st.Properties.Add(prop);
                m_imp.AddedRecords.Add($"{m_cls.ClassId}/{part}/{st.StateId}/{(int)name}/{(int)type}");
            }
            if (value is Margins mg) prop.SetValue(new Margins(mg.Left, mg.Top, mg.Right, mg.Bottom)); else prop.SetValue((int)value);
            Changed(prop, part, st.StateId, "applied-shellstyle", $"{what} {reason}");
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
            var sp = XpShellStyle.ParseRect(m_srcDui.Resolve(DuiCascade.Build(false).TaskList, "padding"))
                     ?? XpShellStyle.ParseRect(m_src.GetAny("tasklist", "atom(tasklist)", "padding"));
            // no padding rule the cascade resolves: the source doesn't state it (its sheet puts it where DUI looks it
            // up, e.g. tasklist [id=atom(tasklist)] under the section sheet), so the template's inset stays. Reading it
            // as 0 pulled XtremeXP's task lists 12px/10px out to the group's edge.
            if (sp == null) return;
            var b = XpShellStyle.ParseRect(m_baseDui.Resolve(DuiCascade.Build(false).TaskList, "padding"))
                    ?? XpShellStyle.ParseRect(m_base.GetAny("tasklist", "atom(tasklist)", "padding")) ?? sp;
            var s = sp;
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

        /// <summary>the port's header CONTENTMARGINS after <see cref="Run"/>: main (special) and normal group. Null where the template has none.</summary>
        public Dictionary<bool, Margins> HeaderContentMargins()
        {
            var d = new Dictionary<bool, Margins>();
            if (m_cls == null) return d;
            if (Find(SpecialHead, 0, IDENTIFIER.CONTENTMARGINS)?.GetValue() is Margins m) d[true] = m;
            if (Find(NormalHead, 0, IDENTIFIER.CONTENTMARGINS)?.GetValue() is Margins n) d[false] = n;
            return d;
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
        /// tile, a gradient (Opus OS paints its groups with the pane's gradient) a stretched strip, and a bitmap (XP's
        /// groups: mode 6, color-keyed) the source's picture, stretched into the clipper with its key see-through. The
        /// bitmap's transparent top band is the header's area on xp (the expando's background covers the header too);
        /// win10 draws the header as a separate element, so the band is cut off before the picture goes in.
        /// </summary>
        private void GroupTile(int part, bool main, string what)
        {
            var img = Find(part, 0, IDENTIFIER.IMAGEFILE);
            if (img == null) return;
            var src = GroupLook(m_srcDui, main);
            var stock = GroupLook(m_baseDui, main);
            if (src.Key == stock.Key) { Restore(img, part, 0, IDENTIFIER.IMAGEFILE, what + " background"); RestoreGeometry(part); return; }
            if (src.Fill == null && src.Pic == null) { m_imp.Report.Warnings.Add($"shellstyle: {what} background \"{src.Key}\" isn't a picture, color or gradient the porter reads; template tile kept."); return; }

            if (src.Pic.HasValue)
            {
                int picId = src.Pic.Value.Id;
                Color? picKey = src.Pic.Value.Key;
                bool picAutoKey = src.Pic.Value.AutoKey;
                byte[] dib = m_src.GetBitmap(picId);
                if (dib == null) { m_imp.Report.Warnings.Add($"shellstyle: {what} background bitmap {picId} is missing; template tile kept."); return; }
                var pic = ImageIdentity.Decode(XpShellBitmap(dib, picKey, picAutoKey));
                int top = 0;
                while (top < pic.Height && Enumerable.Range(0, pic.Width).All(x => (pic.Pixels[top * pic.Width + x] >> 24) == 0)) ++top;
                if (top > 0 && top < pic.Height) pic = pic.Crop(new Rectangle(0, top, pic.Width, pic.Height - top));
                PlaceImage(img, part, ImageIdentity.EncodePngVerified(pic), $"{what} background bitmap {picId}");
                SetGeometry(part, new Margins(0, 0, 0, 0), "the group background is the source's picture, stretched as xp did");
                return;
            }

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
        private (DuiFill? Fill, (int Id, Color? Key, bool AutoKey)? Pic, Color Border, Margins Thickness, string Key) GroupLook(DuiCascade dui, bool main)
        {
            var s = DuiCascade.Build(main);
            var pane = XpShellStyle.ParseFill(dui.Resolve(s.Pane, "background"));
            DuiFill? fill = DuiFill.Clear; (int, Color?, bool)? pic = null; string fillText = "transparent";
            foreach (var node in new[] { s.TaskList, s.Watermark, s.Clipper, s.Expando })
            {
                if (node == null) continue;
                var v = dui.Resolve(node, "background", out var origin);
                if (v == null || origin != node) continue;
                var f = DuiFill.Parse(v);
                if (f == null)
                {
                    // a bitmap (xp's groups: rcbmp(id, 6, key); CrystalBlue's 115/116) or something unparsable.
                    fill = null; fillText = v;
                    if (XpShellStyle.ParseRcBmp(v, out int id, out var rcKey, out bool autoKey)) pic = (id, rcKey, autoKey);
                    break;
                }
                if (!f.Value.Opaque) continue;
                if (f.Value.Stops.Length > 1 && SameFill(f.Value.Stops, pane)) break; // the pane's gradient again: see-through
                fill = f; fillText = v; break;
            }
            var thick = XpShellStyle.ParseRect(dui.Resolve(s.TaskList, "borderthickness")) ?? new Margins(0, 0, 0, 0);
            var border = XpShellStyle.ParseColor(dui.Resolve(s.TaskList, "bordercolor")) ?? Color.Black; // DUI default SC_Black
            bool framed = thick.Left + thick.Top + thick.Right + thick.Bottom > 0;
            string key = $"{fillText}|{(framed ? $"{border.ToArgb()}|{thick.Left},{thick.Top},{thick.Right},{thick.Bottom}" : "")}";
            return (fill, pic, border, thick, key);
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

        /// <summary>bitmap ids an xp shellstyle and a win10 shellstyle share (see <see cref="PatchShellStyleBitmaps"/>).</summary>
        private static bool IsSharedBitmapId(int id) => (id >= 10 && id <= 25) || (id >= 100 && id <= 112);

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
                // only the ids win10's shellstyle keeps from xp: the folder watermarks (10-25) and the task pane art
                // (100-112). Any other number that both files happen to use is the win10 shellstyle's own graphic; its
                // UIFILE references them directly (e.g. graphic(121,2,-1,0,0,0,0)), and swapping in the xp picture of
                // the same number (SevenVG's 200x551 task pane background for an 11x8 win10 glyph) makes Explorer fail
                // to parse the shellstyle at window creation (DUIXMLParserFailure, "Unable to load graphics").
                if (!IsSharedBitmapId(id)) continue;
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

        /// <summary>
        /// Two task pane placements no theme record reaches, because Windows 10 sets them in the shellstyle's own
        /// markup. This rewrites them in the port's copy of the template shellstyle:
        /// <list type="bullet">
        /// <item>the gap between sections. XP stacks expandos in a vertical flow layout and uses the larger of one
        /// expando's bottom margin and the next one's top margin (verticalflowlayout.cpp). Windows 10 instead pads
        /// every Expando at the bottom (UIFILE 1, ExplorerBarStyle). That padding moves by however much the source's
        /// gap differs from stock luna's (15rp). Opus OS: 5rp on xp, so 15 - 10 = 5rp.</item>
        /// <item>header title alignment. Windows 10 places the title button <c>layoutpos="left"</c> in a fill layout
        /// (SHELL32_UIFILE markup), so it sizes to its text. A source title that's centered or right-aligned
        /// (SHADED's <c>middlecenter</c>) gets the button stretched over the header (fill layout, LP_Auto) and
        /// aligned the same way. The box it's aligned in differs: on xp the title is the header's border layout
        /// client, inside the header's borderthickness + padding and its own padding, with the arrow (layoutpos
        /// right) taken off on the right. On Windows 10 it's the header's CONTENTMARGINS
        /// (<paramref name="headerMargins"/>, true = main section) with the arrow overlapping, then the name
        /// button's padding. That padding is set so both boxes have the same center (SHADED: margins 12/3 put the
        /// title 5px right of xp's, where the -10rp right title padding and the 21px arrow shift it left).</item>
        /// <item>task hover background (<paramref name="hoverRecords"/>, from <see cref="TaskHover"/>). Windows 10 builds
        /// "Other Places" links as NavigateButtons, which never get MouseFocused (only MouseWithin), and System
        /// Tasks as ActionButtons drawn with dtb(CommandModule, 3, 2) on hover. Both get dtb() of the hover record.</item>
        /// <item>task pane background picture (<paramref name="paneRecord"/>, from <see cref="PaneBitmap"/>): the
        /// ExplorerBarBackground element's gradient(gtc(ExplorerBar, 0, 0, 3810/3811)) becomes dtb() of that record.</item>
        /// </list>
        /// A template whose markup doesn't have these attributes is left as it is, and that's reported.
        /// Returns notes for the report, and the warnings.
        /// </summary>
        public static (List<string> notes, List<string> warnings) PatchShellStyleLayout(string win10ShellStyle, XpShellStyle source, XpShellStyle stock,
            IReadOnlyDictionary<bool, Margins> headerMargins, IReadOnlyDictionary<bool, (int Part, int State)> hoverRecords,
            (int Part, int State)? paneRecord, PaneWatermarkSet watermarks, bool dryRun, bool taskBand = true,
            bool noSectionIcons = false)
        {
            var notes = new List<string>(); var warnings = new List<string>();
            DuiCascade src = new DuiCascade(source), bas = new DuiCascade(stock);

            // -1: the sheet doesn't state the margin where the cascade looks (xp's rules sit under the section
            // sheets, e.g. expando { margin: rect(0,5,0,0) } in mainsectionss); reading it as 0 collapsed XtremeXP's
            // section gap from stock luna's 15rp to 0rp.
            int Gap(DuiCascade d, XpShellStyle sheet)
            {
                var m = XpShellStyle.ParseRect(d.Resolve(DuiCascade.Build(false).Expando, "margin"))
                        ?? XpShellStyle.ParseRect(sheet.GetAny("expando", null, "margin"));
                return m == null ? -1 : Math.Max(m.Top, m.Bottom);
            }
            int gapSrc = Gap(src, source), gapStock = Gap(bas, stock);
            int gapDelta = gapSrc < 0 || gapStock < 0 ? 0 : gapSrc - gapStock;

            // the title's contentalign as win10 takes it ("middlecenter"), or null when it's left (win10's placement
            // already). The vertical part is the source's too: "topcenter" put SHADED's middlecenter titles against
            // the top edge of the header.
            string Align(bool main)
            {
                var s = DuiCascade.Build(main);
                string v = src.Resolve(s.Title, "contentalign");
                if (v == null) return null;
                // a title padded out past the header (Lion OS X's special group, 200rp in a 200rp section) is hidden
                // on purpose; its alignment isn't meant to be seen.
                var pd = XpShellStyle.ParseRect(src.Resolve(s.Title, "padding"));
                if (pd != null && pd.Left + pd.Right > 64) return null;
                v = v.ToLowerInvariant();
                string horz = v.Contains("center") ? "center" : v.Contains("right") ? "right" : null;
                if (horz == null) return null;
                string vert = v.Contains("middle") ? "middle" : v.Contains("bottom") ? "bottom" : "top";
                return vert + horz;
            }
            var align = new Dictionary<string, string> { ["name_special"] = Align(true), ["name_normal"] = Align(false) };

            // left/right padding of the name button that puts its box's center where xp's title box had it.
            // xp: [header border + padding + title padding, width - (same on the right) - arrow]. win10: [margin +
            // padding, width - margin - padding]. Widths cancel; the rest goes on whichever side keeps it >= 0.
            (int left, int right)? TitlePad(bool main)
            {
                if (headerMargins == null || !headerMargins.TryGetValue(main, out var cm) || cm == null) return null;
                var s = DuiCascade.Build(main);
                Margins R(Func<DuiCascade.Section, DuiCascade.Node> pick, string prop) => XpShellStyle.ParseRect(src.Resolve(pick(s), prop)) ?? new Margins(0, 0, 0, 0);
                Margins b = R(x => x.Header, "borderthickness"), pd = R(x => x.Header, "padding"), tp = R(x => x.Title, "padding");
                Margins ab = R(x => x.Arrow, "borderthickness"), ap = R(x => x.Arrow, "padding");
                int arrow = 0;
                if (XpShellStyle.ParseRcBmp(src.Resolve(s.Arrow, "content"), out int aid, out _) && source.GetBitmap(aid) is byte[] dib && dib.Length >= 8)
                    arrow = BitConverter.ToInt32(dib, 4) + ab.Left + ab.Right + ap.Left + ap.Right;
                int xpLeft = b.Left + pd.Left + tp.Left, xpRight = b.Right + pd.Right + tp.Right + arrow;
                int d = (xpLeft - xpRight) - (cm.Left - cm.Right);
                return d >= 0 ? (d, 0) : (0, -d);
            }
            var pads = new Dictionary<string, (int left, int right)?> { ["name_special"] = TitlePad(true), ["name_normal"] = TitlePad(false) };
            (int Part, int State)? Hover(bool prefer) => hoverRecords == null ? null
                : hoverRecords.TryGetValue(prefer, out var r) ? r : hoverRecords.TryGetValue(!prefer, out var o) ? o : ((int, int)?)null;
            var navHover = Hover(false); var taskHover = Hover(true);

            var updates = new List<(string type, uint id, ushort lang, byte[] data)>();
            var mod = Win32Api.LoadLibraryEx(win10ShellStyle, IntPtr.Zero, Win32Api.LoadLibraryFlags.LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE);
            if (mod == IntPtr.Zero) throw new IOException($"can't open \"{win10ShellStyle}\"");
            try
            {
                // UIFILE 1: the stylesheets. Expando padding, and the alignment of the name buttons.
                foreach (var (lang, text, utf16) in TextResources(mod, "UIFILE", 1))
                {
                    string t = text;
                    int sb = t.IndexOf("<style resid=\"ExplorerBarStyle\">", StringComparison.Ordinal);
                    int se = sb < 0 ? -1 : t.IndexOf("</style>", sb, StringComparison.Ordinal);
                    if (se < 0) { warnings.Add("shellstyle layout: the template's UIFILE 1 has no ExplorerBarStyle sheet; section spacing and title alignment stay."); break; }
                    string sheet = t.Substring(sb, se - sb);
                    if (taskBand)
                    {
                        // the ExplorerBarBackground element is 200rp wide for the left pane; the band spans the window.
                        int wi = sheet.IndexOf(" width=\"200rp\"", StringComparison.Ordinal);
                        if (wi >= 0)
                        {
                            sheet = sheet.Remove(wi, " width=\"200rp\"".Length);
                            notes.Add("shellstyle layout: task pane is a band across the top (xp's My Computer layout), not the 200rp left pane; the ExplorerBarBackground width goes.");
                        }
                        else warnings.Add("shellstyle layout: no width=\"200rp\" on the ExplorerBarBackground element; the band may not span the window.");
                    }
                    if (paneRecord.HasValue)
                    {
                        const string paneFill = "background=\"gradient(gtc(ExplorerBar,0,0,3810), gtc(ExplorerBar,0,0,3811), 1)\"";
                        int pb = sheet.IndexOf("<if class=\"ExplorerBarBackground\">", StringComparison.Ordinal);
                        int pe = pb < 0 ? -1 : sheet.IndexOf("</if>", pb, StringComparison.Ordinal);
                        int pf = pe < 0 ? -1 : sheet.IndexOf(paneFill, pb, pe - pb, StringComparison.Ordinal);
                        if (pf < 0) warnings.Add("shellstyle layout: no ExplorerBarBackground gradient(gtc(ExplorerBar, 0, 0, 3810/3811)) in the template's ExplorerBarStyle; the task pane keeps its gradient instead of the source's picture.");
                        else
                        {
                            sheet = sheet.Substring(0, pf) + $"background=\"dtb(ExplorerBar, {paneRecord.Value.Part}, {paneRecord.Value.State})\"" + sheet.Substring(pf + paneFill.Length);
                            notes.Add($"shellstyle layout: task pane background drawn with dtb(ExplorerBar, {paneRecord.Value.Part}, {paneRecord.Value.State}), the source's background picture.");
                        }
                    }
                    if (gapDelta != 0)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(sheet, @"(<Expando\b[^>]*?\bpadding="")rect\(\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*(\d+)rp\s*\)""");
                        if (!m.Success) warnings.Add("shellstyle layout: the template's Expando has no rect(...rp) padding; section spacing stays.");
                        else
                        {
                            int bottom = Math.Max(0, int.Parse(m.Groups[5].Value) + gapDelta);
                            sheet = sheet.Substring(0, m.Index) + m.Groups[1].Value + $"rect({m.Groups[2].Value},{m.Groups[3].Value},{m.Groups[4].Value},{bottom}rp)\"" + sheet.Substring(m.Index + m.Length);
                            notes.Add($"shellstyle layout: sections {Gap(src, source)}rp apart as on xp (stock luna {Gap(bas, stock)}rp): Expando bottom padding {m.Groups[5].Value}rp -> {bottom}rp.");
                        }
                    }
                    foreach (var kv in align.Where(a => a.Value != null))
                    {
                        string open = $"<if class=\"{kv.Key}\">";
                        int ib = sheet.IndexOf(open, StringComparison.Ordinal);
                        int bb = ib < 0 ? -1 : sheet.IndexOf("<button ", ib, StringComparison.OrdinalIgnoreCase);
                        if (bb < 0 || sheet.IndexOf("</if>", ib, StringComparison.Ordinal) < bb) { warnings.Add($"shellstyle layout: no {kv.Key} button rule in the template's ExplorerBarStyle; that header title stays left."); align[kv.Key] = null; continue; }
                        int be = sheet.IndexOf("/>", bb, StringComparison.Ordinal);
                        string tag = sheet.Substring(bb, be - bb);
                        string newTag = tag.Insert("<button ".Length, $"contentalign=\"{kv.Value}\" ");
                        if (pads[kv.Key].HasValue)
                        {
                            int pl = pads[kv.Key].Value.left, pr = pads[kv.Key].Value.right;
                            // keep the template's top/bottom padding (Luna10: 1rp top).
                            var pm = System.Text.RegularExpressions.Regex.Match(newTag, @"\bpadding=""rect\(\s*[^,]+,\s*([^,]+),\s*[^,]+,\s*([^)]+)\)""");
                            string tb = pm.Success ? $"{pm.Groups[1].Value.Trim()}, {{0}}rp, {pm.Groups[2].Value.Trim()}" : "0, {0}rp, 0";
                            string pad = $"padding=\"rect({pl}rp, {string.Format(tb, pr)})\"";
                            newTag = pm.Success ? newTag.Substring(0, pm.Index) + pad + newTag.Substring(pm.Index + pm.Length) : newTag + " " + pad;
                            notes.Add($"shellstyle layout: {(kv.Key == "name_special" ? "main" : "normal")} header title box padded {pl}rp left, {pr}rp right, centered where xp centers it (xp: header border, padding, title padding and arrow; win10: CONTENTMARGINS).");
                        }
                        sheet = sheet.Substring(0, bb) + newTag + sheet.Substring(be);
                    }
                    if (navHover.HasValue)
                    {
                        int np = navHover.Value.Part, ns = navHover.Value.State;
                        const string nav = "<NavigateButton background=\"argb(0,0,0,0)\"/>";
                        // the place-link row (task_item element) pads its NavigateButton by 5rp/2rp, so a hover drawn on
                        // the button was 10px narrower and 4px shorter than the System Tasks button, which carries the
                        // same padding itself (ActionButton rect(5rp, 2rp, 5rp, 2rp)). The row loses its padding and the
                        // link's icon and label take it over (a NavigateButton's own padding isn't applied: tried live,
                        // the icons moved to the edge), so the bar covers the same box the System Tasks one does.
                        const string row = "<if class=\"task_item\">\r\n\t\t\t\t<viewer padding=\"rect(5rp,2rp,5rp,2rp)\"/>\r\n\t\t\t\t<element padding=\"rect(5rp,2rp,5rp,2rp)\"/>";
                        const string icon = "<if class=\"icon\">\r\n\t\t\t\t<Button Background=\"argb(0,0,0,0)\" cursor=\"hand\" padding=\"rect(0,0,6rp,0)\"/>";
                        const string label = "<if class=\"label_normal\">\r\n\t\t\t\t<Button font=\"gtf(TextStyle, 6, 0)\"";
                        int ni = sheet.IndexOf(nav, StringComparison.Ordinal);
                        int ri = sheet.IndexOf(row, StringComparison.Ordinal);
                        if (ni < 0 || ri < 0 || sheet.IndexOf(icon, StringComparison.Ordinal) < 0 || sheet.IndexOf(label, StringComparison.Ordinal) < 0)
                            warnings.Add("shellstyle layout: no NavigateButton / task_item / icon / label_normal rule in the template's ExplorerBarStyle; place links get no hover background.");
                        else
                        {
                            sheet = sheet.Replace(row, row.Replace("<element padding=\"rect(5rp,2rp,5rp,2rp)\"/>", "<element padding=\"rect(0,0,0,0)\"/>"))
                                         .Replace(icon, icon.Replace("padding=\"rect(0,0,6rp,0)\"", "padding=\"rect(5rp,2rp,6rp,2rp)\""))
                                         .Replace(label, label.Replace("<Button ", "<Button padding=\"rect(0,2rp,5rp,2rp)\" "));
                            ni = sheet.IndexOf(nav, StringComparison.Ordinal);
                            sheet = sheet.Insert(ni + nav.Length, $"\r\n\t\t\t<if mousewithin=\"true\">\r\n\t\t\t\t<NavigateButton background=\"dtb(ExplorerBar, {np}, {ns})\"/>\r\n\t\t\t</if>");
                            notes.Add($"shellstyle layout: place links (NavigateButton) hover with dtb(ExplorerBar, {np}, {ns}), the source's task hover background; the row's 5rp/2rp padding moved onto the link's icon and label so the bar covers it.");
                        }
                    }
                    t = t.Substring(0, sb) + sheet + t.Substring(se);
                    if (taskHover.HasValue)
                    {
                        int tp = taskHover.Value.Part, ts = taskHover.Value.State;
                        // System Tasks (shell32 UIFILE 44): an AJOSplitButton row (BorderLayout, layoutpos top, so as wide
                        // as the task list) holding the ActionButton at layoutpos left (only as wide as its text). xp's
                        // hover covered the whole task row, so the picture goes on the row while the mouse is in it,
                        // and the ActionButton's own hover picture (dtb(CommandModule, 3, 2)) is cleared there.
                        const string tbs = "<style resid=\"TaskButtonStyle\">";
                        int ti = t.IndexOf(tbs, StringComparison.Ordinal);
                        int te = ti < 0 ? -1 : t.IndexOf("</style>", ti, StringComparison.Ordinal);
                        var hm = new System.Text.RegularExpressions.Regex(@"(<if mousefocused=""true"">\s*<Button3d background="")dtb\(CommandModule, 3, 2\)("" font=""gtf\(TextStyle, 6, 2\)"")");
                        string tb = te < 0 ? null : t.Substring(ti, te - ti);
                        if (tb == null || !hm.IsMatch(tb)) warnings.Add("shellstyle layout: no ActionButton hover rule (dtb(CommandModule, 3, 2)) in the template's TaskButtonStyle; task buttons get no hover background.");
                        else
                        {
                            tb = hm.Replace(tb, "${1}argb(0,0,0,0)$2");
                            tb = tb.Insert(tbs.Length, $"\r\n\t\t\t<if mousewithin=\"true\">\r\n\t\t\t\t<AJOSplitButton background=\"dtb(ExplorerBar, {tp}, {ts})\"/>\r\n\t\t\t</if>");
                            t = t.Substring(0, ti) + tb + t.Substring(te);
                            notes.Add($"shellstyle layout: task rows (AJOSplitButton) hover with dtb(ExplorerBar, {tp}, {ts}), the source's task hover background, across the row.");
                        }
                    }
                    if (t != text) updates.Add(("UIFILE", 1u, lang, Encode(t, utf16)));
                }

                // SHELL32_UIFILE: the pane markup. A centered title's button fills its header; with a task hover
                // picture, task rows fill the task list's width as xp's tasks did (xp tasklists are vertical flows
                // whose items stretch to the line; Windows 10's tasklist verticalflowlayout(0,0,0) and the place
                // links' flowlayout() size each row to its text, so the hover would only cover the text).
                var stretch = align.Where(a => a.Value != null).Select(a => a.Key).ToList();
                bool fullRows = navHover.HasValue || taskHover.HasValue;
                {
                    // runs always: the pane watermark band and the music/picture folder backgrounds are patched here even
                    // when the template already fits the source's titles and task rows.
                    var pattern = stretch.Count == 0 ? null : new System.Text.RegularExpressions.Regex($@"(<button class=""(?:{string.Join("|", stretch)})"")\s+layoutpos=""left""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    const string list = "<element id=\"atom(tasklist)\" layoutpos=\"top\" layout=\"verticalflowlayout(0,0,0)\">";
                    const string item = "<element class=\"task_item\" layout=\"flowlayout()\" layoutpos=\"top\" padding=\"rect(0,0,0,0)\">";
                    const string pane = "class=\"ExplorerBarBackground\">";
                    int files = 0, rowFiles = 0, bandFiles = 0, detailsOpen = 0, bandLayoutFiles = 0;

                    foreach (uint id in ResourceIds(mod, "SHELL32_UIFILE"))
                        foreach (var (lang, text, utf16) in TextResources(mod, "SHELL32_UIFILE", id))
                        {
                            string t = pattern == null ? text : pattern.Replace(text, "$1");
                            if (pattern != null && t != text) files++;
                            // which picture this folder's pane gets: the theme-wide one (xp's sectionlist content
                            // picture) on any folder, else the music/picture folders' own picture (xp's shellstyle
                            // bitmaps 12/15), else none (a plain folder's pane had none).
                            // only the theme-wide picture becomes a band; the per-folder pictures are the music/picture
                            // task list's own background (patched below in the stylesheet), like xp's sectionlist content.
                            PaneWatermarkRecord? layoutWatermark = watermarks?.All;
                            {
                                // the Details section's Expando starts Expanded="false"; Windows 10 then shows the section
                                // closed (title row only) until the info module has an item, while xp drew the folder's own
                                // name/type as soon as the folder opened (checked live: without this, opening a folder from
                                // My Computer's My Documents link left "Details" closed; with Expanded="true" it opens).
                                int i = 0;
                                while ((i = t.IndexOf("<Expando", i, StringComparison.OrdinalIgnoreCase)) >= 0)
                                {
                                    int close = t.IndexOf('>', i);
                                    int end = close < 0 ? -1 : t.IndexOf("</Expando>", close, StringComparison.OrdinalIgnoreCase);
                                    if (close < 0 || end < 0) break;
                                    if (t.IndexOf("content=\"Details\"", close, end - close, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        int e = t.IndexOf("Expanded=\"false\"", i, close - i, StringComparison.OrdinalIgnoreCase);
                                        if (e < 0) e = t.IndexOf("expanded=\"false\"", i, close - i, StringComparison.OrdinalIgnoreCase);
                                        if (e >= 0)
                                        {
                                            t = t.Substring(0, e) + "Expanded=\"true\"" + t.Substring(e + "Expanded=\"false\"".Length);
                                            detailsOpen++;
                                        }
                                        break;
                                    }
                                    i = end + 10;
                                }
                            }
                            if (fullRows)
                            {
                                // verticalflowlayout(wrap, xalign, xlinealign): ALIGN_JUSTIFY (3) stretches the column to the
                                // list's width and every row to the column (verticalflowlayout.cpp); a fill layout row
                                // gives its NavigateButton the whole width.
                                string r = t.Replace(list, list.Replace("verticalflowlayout(0,0,0)", "verticalflowlayout(0,3,3)"))
                                            .Replace(item, item.Replace("flowlayout()", "filllayout()"));
                                if (r != t) rowFiles++;
                                t = r;
                            }
                            if (!taskBand && layoutWatermark.HasValue && t.Contains(pane))
                            {
                                // the band is a child of the pane element (borderlayout): pinned to the pane's bottom,
                                // inside its padding, so the picture gets xp's content box (the source's 12rp/1rp padding).
                                // inline picture: the shared ExplorerBarStyle can't give each folder layout its own picture.
                                string band = $"\r\n\t\t\t\t<element class=\"PaneWatermark\" layoutpos=\"bottom\" height=\"{layoutWatermark.Value.Height}rp\" background=\"dtb(ExplorerBar, {layoutWatermark.Value.Part}, {layoutWatermark.Value.State})\"/>";
                                string r = t.Replace(pane, pane + band);
                                if (r != t) bandFiles++;
                                t = r;
                            }
                            if (noSectionIcons && t.Contains("dtb(CommandModule, 12,"))
                            {
                                // the source theme drew no icon beside "Picture Tasks"/"Music Tasks" (no shellstyle
                                // bitmap 10/13/22), but the template's strip would still show its own art there.
                                var icon = new System.Text.RegularExpressions.Regex(
                                    @"\s*<button\s+layoutpos=""left""\s+active=""inactive""\s+content=""themeable\(dtb\(CommandModule,\s*12,\s*\d\),''\)""\s*/>");
                                if (icon.IsMatch(t))
                                {
                                    t = icon.Replace(t, "");
                                    bandLayoutFiles++;
                                }
                            }
                            if (taskBand && t.Contains(pane))
                            {
                                string r = BandLayout(t, t.Contains("content=\"System Tasks\""));
                                if (r != t) bandLayoutFiles++;
                                t = r;
                            }
                            if (t != text) updates.Add(("SHELL32_UIFILE", id, lang, Encode(t, utf16)));
                        }
                    if (stretch.Count > 0)
                    {
                        if (files == 0) warnings.Add("shellstyle layout: the template's SHELL32_UIFILE markup has no left-placed header title buttons; titles stay left.");
                        else notes.Add($"shellstyle layout: header titles aligned as on xp ({string.Join(", ", stretch.Select(k => $"{(k == "name_special" ? "main" : "normal")} {align[k]}"))}), in {files} pane layouts.");
                    }
                    if (fullRows)
                    {
                        if (rowFiles == 0) warnings.Add("shellstyle layout: the template's task lists aren't verticalflowlayout(0,0,0) / flowlayout() rows; the hover background covers only the text.");
                        else notes.Add($"shellstyle layout: task rows stretched to the task list's width (hover covers the row as on xp), in {rowFiles} pane layouts.");
                    }
                    if (!taskBand && watermarks?.All != null)
                    {
                        if (bandFiles == 0) warnings.Add("shellstyle layout: no ExplorerBarBackground element in the template's SHELL32_UIFILE markup; the folder watermark isn't drawn.");
                        else notes.Add($"shellstyle layout: folder watermark on a bottom band of the pane, in {bandFiles} pane layouts (theme-wide picture: {(watermarks?.All != null ? "yes" : "no")}, music/pictures pictures: {(watermarks?.Music != null ? "yes" : "no")}/{(watermarks?.Pictures != null ? "yes" : "no")}).");
                    }
                    if (taskBand)
                    {
                        if (bandLayoutFiles == 0) warnings.Add("shellstyle layout: no left-docked sidebar with ExplorerBarBackground in the template's SHELL32_UIFILE markup; the pane stays on the left.");
                        else notes.Add($"shellstyle layout: task pane moved to a band across the top (xp's WebView layout) in {bandLayoutFiles} folder layouts; the pane's other sections (Other Places, Details) are dropped, the folder's own tasks stay.");
                    }
                    if (detailsOpen > 0)
                        notes.Add($"shellstyle layout: details section open from the start (Expanded=\"true\") in {detailsOpen} pane layouts; win10 leaves it closed until the info module has an item, xp showed the folder's own name/type.");
                    else
                        warnings.Add("shellstyle layout: no Details Expando (Expanded=\"false\") in the template's SHELL32_UIFILE markup; the details section stays closed until an item is selected.");
                }
            }
            finally { Win32Api.FreeLibrary(mod); }

            if (dryRun || updates.Count == 0) return (notes, warnings);
            IntPtr h = Win32Api.BeginUpdateResource(win10ShellStyle, false);
            if (h == IntPtr.Zero) throw new IOException($"can't open \"{win10ShellStyle}\" for resource updates");
            bool ok = true;
            foreach (var u in updates) ok &= Win32Api.UpdateResource(h, u.type, u.id, u.lang, u.data, (uint)u.data.Length);
            if (!Win32Api.EndUpdateResource(h, !ok) || !ok) throw new IOException($"updating the layout in \"{win10ShellStyle}\" failed");
            return (notes, warnings);
        }

        /// <summary>
        /// Turns one folder layout's left sidebar into xp's band across the top. Windows 10's folder template
        /// (SHELL32_UIFILE, "FolderLayout") docks the pane with <c>&lt;element layoutpos="left"&gt;</c> around a
        /// 210rp scrollviewer, then stacks "System Tasks"/"File and Folder Tasks", "Other Places" and "Details" in it;
        /// xp's My Computer/My Documents WebView draws the folder's own tasks as a full-width band under one header.
        /// The sidebar becomes a <c>layoutpos="top"</c> element, its scrollviewer loses the fixed width, the folder tree
        /// module goes (the band has no tree), and only the first Expando (the folder's task list) stays.
        /// </summary>
        private static string BandLayout(string t, bool systemTasks)
        {
            int pane = t.IndexOf("class=\"ExplorerBarBackground\">", StringComparison.Ordinal);
            if (pane < 0) return t;
            int side = t.LastIndexOf("<element layoutpos=\"left\"", pane, StringComparison.Ordinal);
            if (side < 0) return t;
            int sideTag = t.IndexOf('>', side);
            if (sideTag < 0) return t;
            string tag = t.Substring(side, sideTag - side);
            t = t.Substring(0, side) + tag.Replace("layoutpos=\"left\"", "layoutpos=\"top\"") + t.Substring(sideTag);
            int scroller = t.IndexOf("<scrollviewer", side, StringComparison.Ordinal);
            if (scroller >= 0)
            {
                int sc = t.IndexOf('>', scroller);
                if (sc > scroller)
                    t = t.Substring(0, scroller) + t.Substring(scroller, sc - scroller)
                        .Replace(" width=\"210rp\"", "")
                        .Replace(" borderthickness=\"rect(1rp, 0, 0, 1rp)\" bordercolor=\"window\"", "") + t.Substring(sc);
            }
            int tree = t.IndexOf("<ProperTreeModule", side, StringComparison.Ordinal);
            if (tree >= 0)
            {
                int te = t.IndexOf("/>", tree, StringComparison.Ordinal);
                if (te > tree) t = t.Substring(0, tree) + t.Substring(te + 2);
            }
            // only the folder's own section stays: drop the second and later Expandos of the pane element.
            int pane2 = t.IndexOf("class=\"ExplorerBarBackground\">", StringComparison.Ordinal);
            int exp = pane2 < 0 ? -1 : t.IndexOf("<Expando", pane2, StringComparison.Ordinal);
            if (exp < 0) return t;
            var spans = new List<(int Start, int End)>();
            int at = exp;
            while (at >= 0)
            {
                int end = CloseExpandos(t, at);
                if (end < 0) break;
                spans.Add((at, end));
                at = t.IndexOf("<Expando", end, StringComparison.Ordinal);
            }
            for (int k = spans.Count - 1; k >= 1; --k)
                t = t.Substring(0, spans[k].Start) + t.Substring(spans[k].End);
            // xp's band header: 28px tall, title text at x=18 (header padding 13 + the 5 CONTENTMARGINS), the
            // collapse arrow's right edge 13px from the band's edge (measured on the xp reference).
            t = t.Replace("<accessiblebutton id=\"atom(arrow)\" class=\"NormalGroupHeader\" layoutpos=\"top\" layout=\"filllayout()\">",
                "<accessiblebutton id=\"atom(arrow)\" class=\"NormalGroupHeader\" layoutpos=\"top\" layout=\"filllayout()\" height=\"28rp\" padding=\"rect(13rp,0,13rp,0)\">");
            t = t.Replace("<accessiblebutton id=\"atom(arrow)\" class=\"SpecialGroupHeader\" layoutpos=\"bottom\" layout=\"borderlayout()\">",
                "<accessiblebutton id=\"atom(arrow)\" class=\"SpecialGroupHeader\" layoutpos=\"bottom\" layout=\"borderlayout()\" height=\"28rp\" padding=\"rect(13rp,0,13rp,0)\">");
            if (!systemTasks)
            {
                // sections whose tasks are plain links (Picture Tasks) instead of the folder command module: put them
                // in one row as xp's band did. The command module sections (File and Folder Tasks) can't reflow: the
                // module lays its own command elements out in a column, and a row/border layout on the module, its
                // clipper or its tasklist, and hosting the module directly changed nothing (checked live).
                int tlist = t.IndexOf("<element id=\"atom(tasklist)\"", pane2, StringComparison.Ordinal);
                int tclose = tlist < 0 ? -1 : t.IndexOf('>', tlist);
                int tend = tlist < 0 ? -1 : CloseElement(t, tlist);
                if (tlist >= 0 && tclose > tlist && tend > tclose
                    && t.IndexOf("<FolderBandModule", tlist, tend - tlist, StringComparison.Ordinal) < 0)
                {
                    string listTag = t.Substring(tlist, tclose - tlist);
                    if (listTag.Contains("layout=\""))
                        listTag = System.Text.RegularExpressions.Regex.Replace(listTag, "layout=\"[^\"]*\"", "layout=\"rowlayout()\"");
                    else listTag += " layout=\"rowlayout()\"";
                    listTag += " height=\"22rp\"";
                    t = t.Substring(0, tlist) + listTag + t.Substring(tclose);
                }
                return t;
            }

            // "System Tasks" (My Computer). Windows 10 draws its three commands with a FolderBandModule, whose native
            // toolbar is vertical inside a task pane: the band showed them three rows tall. xp's band puts them side
            // by side in one row. The three commands are the same xp ran, so each becomes a NavigateButton in a row
            // layout (shell targets: System Properties, Add or Remove Programs, Control Panel).
            int list = t.IndexOf("<element id=\"atom(tasklist)\"", pane2, StringComparison.Ordinal);
            if (list >= 0)
            {
                int listTag = t.IndexOf('>', list);
                int listEnd = CloseElement(t, list);
                int mod = t.IndexOf("<FolderBandModule", list, StringComparison.Ordinal);
                if (listTag > list && listEnd > listTag && mod >= 0 && mod < listEnd)
                {
                    string head = "<element id=\"atom(tasklist)\" layoutpos=\"top\" height=\"22rp\" padding=\"rect(10rp,0,0,0)\" layout=\"rowlayout()\">";
                    string tail = "</element>";
                    string buttons =
                        "\r\n\t\t\t\t\t\t\t<element class=\"task_item\" layout=\"flowlayout()\" layoutpos=\"top\" padding=\"rect(0,0,0,0)\">\r\n" +
                        "\t\t\t\t\t\t\t\t<NavigateButton layout=\"borderlayout()\" shellexecute=\"sysdm.cpl\">\r\n" +
                        "\t\t\t\t\t\t\t\t\t<Button class=\"icon\" layoutpos=\"left\" content=\"icon(253, sysmetric(49), sysmetric(50), library(shell32.dll))\" accessible=\"true\" accrole=\"pushbutton\"/>\r\n" +
                        "\t\t\t\t\t\t\t\t\t<Button class=\"label_normal\" layoutpos=\"left\" content=\"View system information\" accessible=\"true\" accrole=\"pushbutton\"/>\r\n" +
                        "\t\t\t\t\t\t\t\t</NavigateButton>\r\n" +
                        "\t\t\t\t\t\t\t</element>\r\n" +
                        "\t\t\t\t\t\t\t<element class=\"task_item\" layout=\"flowlayout()\" layoutpos=\"top\" padding=\"rect(0,0,0,0)\">\r\n" +
                        "\t\t\t\t\t\t\t\t<NavigateButton layout=\"borderlayout()\" shellexecute=\"appwiz.cpl\">\r\n" +
                        "\t\t\t\t\t\t\t\t\t<Button class=\"icon\" layoutpos=\"left\" content=\"icon(271, sysmetric(49), sysmetric(50), library(shell32.dll))\" accessible=\"true\" accrole=\"pushbutton\"/>\r\n" +
                        "\t\t\t\t\t\t\t\t\t<Button class=\"label_normal\" layoutpos=\"left\" content=\"Add or remove programs\" accessible=\"true\" accrole=\"pushbutton\"/>\r\n" +
                        "\t\t\t\t\t\t\t\t</NavigateButton>\r\n" +
                        "\t\t\t\t\t\t\t</element>\r\n" +
                        "\t\t\t\t\t\t\t<element class=\"task_item\" layout=\"flowlayout()\" layoutpos=\"top\" padding=\"rect(0,0,0,0)\">\r\n" +
                        "\t\t\t\t\t\t\t\t<NavigateButton layout=\"borderlayout()\" shellexecute=\"control.exe\">\r\n" +
                        "\t\t\t\t\t\t\t\t\t<Button class=\"icon\" layoutpos=\"left\" content=\"icon(137, sysmetric(49), sysmetric(50), library(shell32.dll))\" accessible=\"true\" accrole=\"pushbutton\"/>\r\n" +
                        "\t\t\t\t\t\t\t\t\t<Button class=\"label_normal\" layoutpos=\"left\" content=\"Change a setting\" accessible=\"true\" accrole=\"pushbutton\"/>\r\n" +
                        "\t\t\t\t\t\t\t\t</NavigateButton>\r\n" +
                        "\t\t\t\t\t\t\t</element>";
                    t = t.Substring(0, list) + head + buttons + "\r\n\t\t\t\t\t\t" + tail + t.Substring(listEnd);
                }
            }
            return t;
        }

        /// <summary>the index just past the &lt;/element&gt; matching the element tag at <paramref name="start"/>.</summary>
        private static int CloseElement(string t, int start)
        {
            int depth = 0, at = start;
            while (true)
            {
                int open = t.IndexOf("<element", at, StringComparison.OrdinalIgnoreCase);
                int close = t.IndexOf("</element>", at, StringComparison.OrdinalIgnoreCase);
                if (close < 0) return -1;
                if (open >= 0 && open < close) { depth++; at = open + 8; continue; }
                depth--;
                at = close + "</element>".Length;
                if (depth <= 0) return at;
            }
        }

        /// <summary>the index just past the &lt;/Expando&gt; matching the &lt;Expando ...&gt; at <paramref name="start"/>.</summary>
        private static int CloseExpandos(string t, int start)
        {
            int depth = 0, at = start;
            while (true)
            {
                int open = t.IndexOf("<Expando", at, StringComparison.OrdinalIgnoreCase);
                int close = t.IndexOf("</Expando>", at, StringComparison.OrdinalIgnoreCase);
                if (close < 0) return -1;
                if (open >= 0 && open < close) { depth++; at = open + 8; continue; }
                depth--;
                at = close + "</Expando>".Length;
                if (depth <= 0) return at;
            }
        }

        private static byte[] Encode(string t, bool utf16)
            => utf16 ? System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes(t)).ToArray() : System.Text.Encoding.UTF8.GetBytes(t);

        private static List<uint> ResourceIds(IntPtr mod, string type)
        {
            var ids = new List<uint>();
            var typePtr = Marshal.StringToHGlobalUni(type);
            try
            {
                Win32Api.EnumResourceNamesEx(mod, typePtr, (m, t, name, l) =>
                {
                    long v = name.ToInt64();
                    if (v > 0 && v <= 0xFFFF) ids.Add((uint)v);
                    return true;
                }, IntPtr.Zero, Win32Api.EnumResourceFlags.RESOURCE_ENUM_LN, 0);
            }
            finally { Marshal.FreeHGlobal(typePtr); }
            return ids;
        }

        /// <summary>every language of one text resource: UTF-16 with a BOM, otherwise UTF-8/ANSI markup (Luna10's).</summary>
        private static List<(ushort lang, string text, bool utf16)> TextResources(IntPtr mod, string type, uint id)
        {
            var langs = new List<ushort>();
            Win32Api.EnumResourceLanguagesEx(mod, type, "#" + id, (m, t, n, lang, l) => { langs.Add(lang); return true; },
                IntPtr.Zero, Win32Api.EnumResourceFlags.RESOURCE_ENUM_LN, 0);
            var list = new List<(ushort, string, bool)>();
            foreach (var lang in langs)
            {
                IntPtr r = Win32Api.FindResourceEx(mod, type, id, lang);
                if (r == IntPtr.Zero) continue;
                uint size = Win32Api.SizeofResource(mod, r);
                var buf = new byte[size];
                Marshal.Copy(Win32Api.LockResource(Win32Api.LoadResource(mod, r)), buf, 0, (int)size);
                bool utf16 = size >= 2 && buf[0] == 0xFF && buf[1] == 0xFE;
                list.Add((lang, utf16 ? System.Text.Encoding.Unicode.GetString(buf, 2, buf.Length - 2) : System.Text.Encoding.UTF8.GetString(buf), utf16));
            }
            return list;
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
