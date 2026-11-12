using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace libmsstyle.Import
{
    /// <summary>
    /// DWMWindow is a single PNG atlas (Common Properties, IMAGEATLAS, DISKSTREAM) plus one
    /// ATLASRECT per part. Win7 and win10 number the parts differently, so parts are matched by
    /// name, never by id.
    ///
    /// The mapping only works because the template's atlas is the stock win7 atlas (checked, not
    /// assumed): then every template rect is a stock win7 rect and can be traced back to the win7
    /// part that owns it, even for win10-only parts that reuse a win7 sprite (the DARK glyphs, the
    /// extra caption button backgrounds 88-91). The source atlas replaces the template atlas and
    /// each rect is pointed at the source's sprite for the same win7 part. A template sprite that
    /// can't be traced or whose new geometry doesn't fit is copied over from the template atlas
    /// unchanged, so no frame/caption/button part is ever guessed.
    /// XP instead has Window bitmap parts rather than a DWM atlas. Its explicit adapter rasterizes
    /// matching frame faces and button/glyph layers into the existing target sprite slots, retaining
    /// target atlas geometry and unrelated sprites. Shared target glyph/background contracts and
    /// compositor placement/shadows are reported limitations, not claims of XP-native rendering.
    /// </summary>
    internal class DwmImport
    {
        private readonly Win7StyleImporter m_imp;
        private readonly DwmReport m_rep;

        private class PartData
        {
            public string Name;
            public int Id;
            public List<SemanticEntry> Entries = new List<SemanticEntry>();
            public IEnumerable<SemanticEntry> Rects => Entries.Where(e => e.NameId == (int)IDENTIFIER.ATLASRECT && e.TypeId == (int)IDENTIFIER.RECTTYPE);
            public SemanticEntry Find(int state, string prop, int typeId, int occurrence)
            {
                return Entries.FirstOrDefault(e => e.StateId == state && e.PropertyName == prop && e.TypeId == typeId && e.Occurrence == occurrence);
            }
        }

        private class RectPlan
        {
            public PartData Part;
            public SemanticEntry Entry;
            public Rectangle TargetRect;
            public string Src;
            public Rectangle SourceRect;
            public string Method;
            public string Note;
        }

        public DwmImport(Win7StyleImporter importer)
        {
            m_imp = importer;
            m_rep = importer.Report.Dwm;
        }

        private static Dictionary<string, PartData> Group(SemanticModel m)
        {
            var d = new Dictionary<string, PartData>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in m.Entries.Where(e => string.Equals(e.ClassName, Win7StyleImporter.DwmClass, StringComparison.OrdinalIgnoreCase)))
            {
                if (!d.TryGetValue(e.PartName, out var p))
                {
                    p = new PartData { Name = e.PartName, Id = e.PartId };
                    d[e.PartName] = p;
                }
                p.Entries.Add(e);
            }
            return d;
        }

        internal static Rectangle RectOf(SemanticEntry e)
        {
            var m = (Margins)e.Property.GetValue();
            return Rectangle.FromLTRB(m.Left, m.Top, m.Right, m.Bottom);
        }

        private static SemanticEntry AtlasEntry(Dictionary<string, PartData> parts)
        {
            return parts.Values.Where(p => p.Id == 0)
                .SelectMany(p => p.Entries)
                .FirstOrDefault(e => e.TypeId == (int)IDENTIFIER.DISKSTREAM);
        }

        public void Run()
        {
            var report = m_imp.Report;
            var b = Group(m_imp.Base);
            var s = Group(m_imp.Source);
            var t = Group(m_imp.Target);

            if (t.Count == 0)
            {
                report.Errors.Add("template has no DWMWindow class; it can't be a working win10 style.");
                return;
            }
            var ta = AtlasEntry(t);
            var ba = AtlasEntry(b);
            var sa = AtlasEntry(s);
            if (ta == null)
            {
                report.Errors.Add("template DWMWindow has no IMAGEATLAS stream.");
                return;
            }
            m_rep.TemplateAtlasId = ta.ResourceId;
            m_rep.NewAtlasId = ta.ResourceId;
            if (KeyPattern.Matches(m_imp.Options.Exclude, Win7StyleImporter.DwmClass, ta.PartName, ta.PropertyName))
            {
                report.Notes.Add("DWMWindow atlas excluded by rule; template frames kept.");
                return;
            }
            if (m_imp.Options.SourcePlatform == Platform.WinXP)
            {
                RunXp(t, ta);
                return;
            }
            if (ba == null || sa == null)
            {
                report.Warnings.Add("DWMWindow not imported: base or source has no DWMWindow IMAGEATLAS. template frames kept.");
                return;
            }

            var tAtlas = m_imp.Decoded(m_imp.Target, "STREAM", ta.ResourceId);
            var bAtlas = m_imp.Decoded(m_imp.Base, "STREAM", ba.ResourceId);
            var sAtlas = m_imp.Decoded(m_imp.Source, "STREAM", sa.ResourceId);
            if (tAtlas == null || bAtlas == null || sAtlas == null)
            {
                report.Errors.Add("a DWMWindow atlas is missing or isn't a decodable png; DWM data can't be checked.");
                return;
            }
            m_rep.SourceAtlasSize = $"{sAtlas.Width}x{sAtlas.Height}";
            m_rep.TemplateAtlasIsStockWin7 = Win7StyleImporter.NearlyEqual(tAtlas, bAtlas, 0);
            m_rep.SourceAtlasChanged = !Win7StyleImporter.NearlyEqual(sAtlas, bAtlas, 0);

            if (!m_rep.TemplateAtlasIsStockWin7)
            {
                report.Warnings.Add("DWMWindow not imported: the template atlas isn't the stock win7 atlas, so its rects can't be traced to win7 parts. template frames kept.");
                return;
            }

            var plans = new List<RectPlan>();
            var srcOfPart = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tp in t.Values.OrderBy(p => p.Id))
            {
                foreach (var re in tp.Rects)
                {
                    var plan = new RectPlan { Part = tp, Entry = re, TargetRect = RectOf(re) };
                    plans.Add(plan);
                    if (plan.TargetRect.Width <= 0 || plan.TargetRect.Height <= 0)
                    {
                        plan.Method = "none";
                        plan.Note = "empty rect in the template, left alone";
                        continue;
                    }
                    ResolveSource(plan, b, s, sAtlas);
                    if (plan.Src != null && !srcOfPart.ContainsKey(tp.Name)) srcOfPart[tp.Name] = plan.Src;
                }
            }

            // parts without a rect still carry colors, glow sizes, border settings...
            foreach (var tp in t.Values)
            {
                if (!srcOfPart.ContainsKey(tp.Name) && b.ContainsKey(tp.Name)) srcOfPart[tp.Name] = tp.Name;
            }

            // property merge per part, with rollback when the merged geometry doesn't fit the sprite.
            foreach (var tp in t.Values.OrderBy(p => p.Id))
            {
                if (!srcOfPart.TryGetValue(tp.Name, out var src)) continue;
                if (plans.Any(p => p.Part == tp && p.Method == "fallback"))
                {
                    // the template sprite stays, so its margins/frame counts must stay with it.
                    report.Notes.Add($"DWMWindow {tp.Name}: template sprite kept, template properties kept with it.");
                    continue;
                }
                b.TryGetValue(src, out var bp);
                s.TryGetValue(src, out var sp);
                if (bp == null || sp == null) continue;

                var partPlans = plans.Where(p => p.Part == tp && p.Method != null && p.Method != "fallback" && p.Method != "none").ToList();
                MergePart(tp, bp, sp, src, partPlans.Count > 0);

                // the sprite came over with its own frame count and margins, so whatever size it has,
                // it's described the way the source described it. an odd height is fine for the editor
                // (it slices proportionally, 129px in 2 frames is 64 + 65); how uDWM 19041 rounds that
                // isn't verified. a frame with nothing left after uDWM's 1px inset, or margins wider than
                // the frame, only gets a warning, since the source shipped the same numbers on win7.
                foreach (var plan in partPlans)
                {
                    if (GeometryFits(tp, plan.Entry.StateId, plan.SourceRect, out string why)) continue;
                    report.Warnings.Add($"DWMWindow {tp.Name}: {why}; same as the source ships it, check this part in the VM.");
                    m_imp.GeometryWaivers.Add(tp.Name);
                    report.Count("dwm-geometry-warning");
                }
            }

            BuildAtlas(plans, ta, sa, tAtlas, sAtlas);
            var keptHighlights = PreserveHighlights(s, sAtlas);

            var usedSrc = new HashSet<string>(srcOfPart.Values, StringComparer.OrdinalIgnoreCase);
            usedSrc.UnionWith(keptHighlights);
            foreach (var bp in b.Values.Where(p => p.Id != 0 && !usedSrc.Contains(p.Name)).OrderBy(p => p.Id))
            {
                m_rep.Win7OnlyParts.Add(bp.Name);
                bool changed = s.TryGetValue(bp.Name, out var sp2) && bp.Entries.Any(e =>
                {
                    var se = sp2.Find(e.StateId, e.PropertyName, e.TypeId, e.Occurrence);
                    return se != null && e.NameId != (int)IDENTIFIER.ATLASRECT && !m_imp.Same(m_imp.Base, e, m_imp.Source, se);
                });
                if (changed)
                    report.Notes.Add($"DWMWindow {bp.Name}: source customized a win7-only part (no win10 consumer); dropped.");
            }
        }
        private sealed class XpFramePlan
        {
            public PartData Part;
            public SemanticEntry Entry;
            public Rectangle Rect;
            public string SourcePart;
            public ArgbImage Sprite;
            public Margins[] Margins;
            /// <summary>the source's CONTENTMARGINS per frame, or null where it declared none. The sprite's
            /// pixels replace the template's, so the padding that places text inside them must come with it.</summary>
            public Margins[] ContentMargins;
            public bool Button;
            public bool Glyph;
            public string Note;
        }

        // Squeegee/no-shadow are compositor variants of the same frame, not XP maximized
        // captions. Keeping their target slots and state counts avoids changing uDWM's packing.
        private static string XpFramePart(string name)
        {
            bool small = name.StartsWith("SMALL", StringComparison.OrdinalIgnoreCase);
            string n = small ? name.Substring(5) : name;
            if (n.StartsWith("TOPFRAME", StringComparison.OrdinalIgnoreCase)) return small ? "SMALLCAPTION" : "CAPTION";
            if (n.StartsWith("BOTTOMFRAME", StringComparison.OrdinalIgnoreCase)) return small ? "SMALLFRAMEBOTTOM" : "FRAMEBOTTOM";
            if (n.StartsWith("LEFTFRAME", StringComparison.OrdinalIgnoreCase)) return small ? "SMALLFRAMELEFT" : "FRAMELEFT";
            if (n.StartsWith("RIGHTFRAME", StringComparison.OrdinalIgnoreCase)) return small ? "SMALLFRAMERIGHT" : "FRAMERIGHT";
            if (n.StartsWith("BUTTON", StringComparison.OrdinalIgnoreCase))
            {
                if (n.Contains("CLOSE")) return small ? "SMALLCLOSEBUTTON" : "CLOSEBUTTON";
                if (n.Contains("MIN")) return "MINBUTTON";
                if (n.Contains("MAX")) return "MAXBUTTON";
                if (n.Contains("RESTORE")) return "RESTOREBUTTON";
                if (n.Contains("HELP")) return "HELPBUTTON";
                if (n.Contains("CAPTION")) return "MINBUTTON";
            }
            return null;
        }

        private void RunXp(Dictionary<string, PartData> targetParts, SemanticEntry atlasEntry)
        {
            var report = m_imp.Report;
            var template = m_imp.Decoded(m_imp.Target, "STREAM", atlasEntry.ResourceId);
            if (template == null)
            {
                report.Errors.Add("template DWMWindow atlas is missing or isn't a decodable png.");
                return;
            }
            var window = m_imp.Source.Style.Classes.Values.FirstOrDefault(c => string.Equals(c.ClassName, "Window", StringComparison.OrdinalIgnoreCase));
            var names = window == null ? null : SemanticModel.PartNames(window.ClassName, Platform.WinXP);
            var plans = new List<XpFramePlan>();
            foreach (var part in targetParts.Values.OrderBy(p => p.Id))
                foreach (var entry in part.Rects)
                {
                    var plan = new XpFramePlan
                    {
                        Part = part, Entry = entry, Rect = RectOf(entry), SourcePart = XpFramePart(part.Name),
                        Button = part.Name.IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) >= 0,
                        Glyph = part.Name.IndexOf("GLYPH", StringComparison.OrdinalIgnoreCase) >= 0
                    };
                    plans.Add(plan);
                    if (plan.Rect.Width <= 0 || plan.Rect.Height <= 0) continue;
                    if (!InsideAtlas(template, plan.Rect))
                    {
                        report.Errors.Add($"DWMWindow {part.Name}: template rect {plan.Rect} is outside its atlas.");
                        continue;
                    }
                    if (plan.SourcePart == null) { plan.Note = "no XP Window frame counterpart; template sprite retained"; continue; }
                    if (KeyPattern.Matches(m_imp.Options.Exclude, Win7StyleImporter.DwmClass, part.Name, "ATLASRECT")
                        || KeyPattern.Matches(m_imp.Options.Exclude, Win7StyleImporter.DwmClass, part.Name, "SIZINGMARGINS")
                        || KeyPattern.Matches(m_imp.Options.Exclude, "Window", plan.SourcePart, "IMAGEFILE"))
                    {
                        plan.Note = "frame picture or its paired margins excluded by rule";
                        continue;
                    }
                    int sourcePart = names == null ? -1 : names.Where(n => string.Equals(n.Value, plan.SourcePart, StringComparison.OrdinalIgnoreCase)).Select(n => n.Key).DefaultIfEmpty(-1).First();
                    if (sourcePart < 0 || !window.Parts.ContainsKey(sourcePart))
                    {
                        plan.Note = "source has no Window " + plan.SourcePart;
                        continue;
                    }
                    var targetClass = m_imp.Target.Style.Classes[entry.ClassId];
                    int count = EffectiveCount(m_imp.Target.Style, targetClass, part.Id, entry.StateId);
                    if (part.Name.IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        BuildXpButton(plan, window, sourcePart, count);
                        continue;
                    }
                    if (count != 2 || plan.Rect.Width < 3 || plan.Rect.Height / count < 3)
                    {
                        plan.Note = "target frame is not a nonempty active/inactive two-frame strip";
                        continue;
                    }
                    var sprite = new ArgbImage(plan.Rect.Width, plan.Rect.Height);
                    var margins = new Margins[count];
                    var content = new Margins[count];
                    bool complete = true;
                    for (int i = 0; i < count; ++i)
                    {
                        int state = entry.StateId == 0 ? i + 1 : entry.StateId;
                        var imageProp = SemanticModel.FindEffective(m_imp.Source.Style, window, sourcePart, state, IDENTIFIER.IMAGEFILE);
                        var source = imageProp?.GetValue() is int imageId ? m_imp.Decoded(m_imp.Source, "IMAGE", imageId & 0xFFFF) : null;
                        int sourceCount = EffectiveCount(m_imp.Source.Style, window, sourcePart, state);
                        bool horizontal = SemanticModel.FindEffective(m_imp.Source.Style, window, sourcePart, state, IDENTIFIER.IMAGELAYOUT)?.GetValue() is int layout ? layout == 1 : true;
                        int fw = source == null ? 0 : source.Width / (horizontal ? sourceCount : 1);
                        int fh = source == null ? 0 : source.Height / (horizontal ? 1 : sourceCount);
                        if (fw < 1 || fh < 1 || (sourceCount != 1 && sourceCount != 2))
                        {
                            complete = false;
                            plan.Note = "source frame picture is missing or does not describe one frame or an active/inactive pair";
                            break;
                        }
                        // XP states 1/2 select the first/second frame, even when IMAGEFILE is
                        // explicitly overridden on that state. One-frame art is shared by both.
                        int index = sourceCount == 1 ? 0 : Math.Min(state - 1, sourceCount - 1);
                        var frame = source.Crop(new Rectangle(horizontal ? index * fw : 0, horizontal ? 0 : index * fh, fw, fh));
                        ApplyXpColorKey(frame, window, sourcePart, state);
                        // uDWM stretches an atlas sprite: no DWMWindow part carries a SIZINGTYPE record (the
                        // template has none, and none is written), so the compositor always uses its STRETCH
                        // default. Tiling the interior here would hand it a tiled-looking body that it then
                        // stretches again. XP's own TILE is not expressible in the atlas.
                        bool tile = false;
                        int top = i * plan.Rect.Height / count, bottom = (i + 1) * plan.Rect.Height / count;
                        var rendered = SynthesizeXpFrameStrip(frame, 1, false, plan.Rect.Width, bottom - top, 1,
                            EffectiveSizing(m_imp.Source.Style, window, sourcePart, state), tile, out margins[i]);
                        if (rendered == null) { complete = false; plan.Note = "source frame cannot be nine-gridded into the target slot"; break; }
                        sprite.Blit(rendered, 0, top);
                        content[i] = SemanticModel.FindEffective(m_imp.Source.Style, window, sourcePart, state, IDENTIFIER.CONTENTMARGINS)?.GetValue() is Margins cm ? cm : null;
                    }
                    if (complete) { plan.Sprite = sprite; plan.Margins = margins; plan.ContentMargins = content; }
                }

            // Reject overlaps to a fixed point: retaining one sprite can invalidate an alias
            // that previously agreed with its synthesized pixels, but not its template pixels.
            bool rejected;
            do
            {
                var conflicts = new HashSet<XpFramePlan>();
                // A face/glyph pair is one visual. A failed or excluded layer must not leave
                // XP glyphs over a template face (or a transparent face with template glyphs).
                foreach (var group in plans.Where(p => p.Button && p.SourcePart != null).GroupBy(p =>
                    p.SourcePart == "CLOSEBUTTON" || p.SourcePart == "SMALLCLOSEBUTTON" ? p.SourcePart : "CAPTION"))
                    if (group.Any(p => p.Sprite == null))
                        foreach (var plan in group.Where(p => p.Sprite != null)) conflicts.Add(plan);
                for (int i = 0; i < plans.Count; ++i)
                    for (int j = i + 1; j < plans.Count; ++j)
                    {
                        var a = plans[i]; var b = plans[j];
                        if ((a.Sprite == null && b.Sprite == null) || !InsideAtlas(template, a.Rect) || !InsideAtlas(template, b.Rect)) continue;
                        var intersection = Rectangle.Intersect(a.Rect, b.Rect);
                        if (intersection.Width <= 0 || intersection.Height <= 0) continue;
                        var ai = a.Sprite ?? template; var bi = b.Sprite ?? template;
                        var ar = a.Sprite == null ? intersection : new Rectangle(intersection.X - a.Rect.X, intersection.Y - a.Rect.Y, intersection.Width, intersection.Height);
                        var br = b.Sprite == null ? intersection : new Rectangle(intersection.X - b.Rect.X, intersection.Y - b.Rect.Y, intersection.Width, intersection.Height);
                        if (ai.RegionEquals(ar, bi, br)) continue;
                        if (a.Sprite != null) conflicts.Add(a);
                        if (b.Sprite != null) conflicts.Add(b);
                    }
                foreach (var plan in conflicts) { plan.Sprite = null; plan.Note = "target atlas overlap would change another consumer's pixels; template sprite retained"; }
                rejected = conflicts.Count != 0;
            } while (rejected);

            var output = new ArgbImage(template.Width, template.Height, (uint[])template.Pixels.Clone());
            foreach (var plan in plans.Where(p => p.Sprite != null)) output.Blit(plan.Sprite, plan.Rect.X, plan.Rect.Y);
            foreach (var plan in plans)
            {
                if (!InsideAtlas(template, plan.Rect)) continue;
                bool imported = plan.Sprite != null;
                var expected = imported ? plan.Sprite : template.Crop(plan.Rect);
                m_imp.SpriteChecks.Add(new SpriteExpectation { Part = plan.Part.Name, OutputRect = plan.Rect, Expected = expected });
                m_rep.Parts.Add(new DwmPartMapping
                {
                    TargetPartId = plan.Part.Id, TargetPart = plan.Part.Name, SourcePart = imported ? "Window|" + plan.SourcePart : null,
                    Method = imported ? (plan.Button ? "xp-button" : "xp-frame") : "fallback", TargetRect = plan.Rect.ToString(), NewRect = plan.Rect.ToString(),
                    Note = imported ? (plan.Note ?? "XP active/inactive artwork nine-gridded into the unchanged target slot, with per-frame 1px edge padding and fitted source margins") : plan.Note
                });
                if (!imported)
                {
                    if (!m_rep.FallbackParts.Contains(plan.Part.Name)) m_rep.FallbackParts.Add(plan.Part.Name);
                    if (plan.SourcePart != null) report.Warnings.Add($"DWMWindow {plan.Part.Name}: {plan.Note}.");
                    continue;
                }
                var cls = m_imp.Target.Style.Classes[plan.Entry.ClassId];
                PutXpProperty(cls, plan.Part, plan.Entry.StateId, IDENTIFIER.SIZINGMARGINS, IDENTIFIER.MARGINS, plan.Margins[0]);
                if (plan.ContentMargins != null && plan.ContentMargins[0] is Margins firstContent)
                    PutXpProperty(cls, plan.Part, plan.Entry.StateId, IDENTIFIER.CONTENTMARGINS, IDENTIFIER.MARGINS, firstContent);
                for (int i = 0; i < plan.Margins.Length; ++i)
                {
                    int state = plan.Entry.StateId == 0 ? i + 1 : plan.Entry.StateId;
                    PutXpProperty(cls, plan.Part, state, IDENTIFIER.SIZINGMARGINS, IDENTIFIER.MARGINS, plan.Margins[i]);
                    // the sprite's pixels are XP's now, so the padding that places the caption text inside them
                    // has to come with it: the template's CONTENTMARGINS was fitted to the template's own frame
                    // (Elfin2's caption art has a 17px top band; the template's record put the title at the top).
                    if (plan.ContentMargins != null && plan.ContentMargins[i] is Margins cm)
                        PutXpProperty(cls, plan.Part, state, IDENTIFIER.CONTENTMARGINS, IDENTIFIER.MARGINS, cm);
                }
                // All imported frame/face pixels already contain their authored color and alpha.
                // Do not apply inherited glass tint or inactive attenuation on top of them.
                foreach (int state in plan.Part.Entries.Select(e => e.StateId)
                    .Concat(Enumerable.Range(0, plan.Button ? 5 : 3)).Distinct())
                {
                    PutXpProperty(cls, plan.Part, state, IDENTIFIER.OPACITY, IDENTIFIER.INT, 100);
                    PutXpProperty(cls, plan.Part, state, IDENTIFIER.COLORIZATIONOPACITY, IDENTIFIER.INT, 0);
                }
                if (plan.SourcePart.EndsWith("CAPTION", StringComparison.Ordinal))
                {
                    // The pixels already contain XP's colors. Template glass opacity/tint/glow
                    // must not wash out the inactive frame or recolor the source caption.
                    foreach (int state in plan.Part.Entries.Select(e => e.StateId).Concat(new[] { 0, 1, 2 }).Distinct())
                    {
                        PutXpProperty(cls, plan.Part, state, IDENTIFIER.TEXTGLOWSIZE, IDENTIFIER.INT, 0);
                        PutXpProperty(cls, plan.Part, state, IDENTIFIER.GLOWINTENSITY, IDENTIFIER.INT, 0);
                        int xpState = state == 2 || state == 4 ? 2 : 1;
                        var color = XpSysColor(xpState == 2 ? IDENTIFIER.INACTIVECAPTIONTEXT : IDENTIFIER.CAPTIONTEXT);
                        if (color != null) PutXpProperty(cls, plan.Part, state, IDENTIFIER.TEXTCOLOR, IDENTIFIER.COLOR, color.Value);
                    }
                }
                report.Count(plan.Button ? "dwm-rect-xp-button" : "dwm-rect-xp-frame");
            }
            if (!plans.Any(p => p.Sprite != null))
            {
                report.Warnings.Add("DWMWindow XP frame synthesis imported no frames; see per-part fallback reasons.");
                return;
            }
            var png = ImageIdentity.EncodePngVerified(output);
            int id = m_imp.PlaceResource("STREAM", atlasEntry.ResourceId, png, ImageIdentity.Analyze(png));
            atlasEntry.Property.SetValue(id);
            m_imp.MarkTouched(atlasEntry.Property, $"DWMWindow|{atlasEntry.PartName}|S{atlasEntry.StateId}|{atlasEntry.PropertyName}");
            m_imp.Decide(atlasEntry, "applied-xp-frame-atlas", "XP Window frame and caption-button artwork synthesized without relocating target sprites or changing their frame counts", null, null, atlasEntry, id.ToString());
            m_rep.NewAtlasId = id;
            m_rep.NewAtlasSize = $"{output.Width}x{output.Height}";
            m_rep.SourceAtlasChanged = !Win7StyleImporter.NearlyEqual(template, output, 0);
            m_rep.AtlasReencoded = true;
            if (window != null && plans.Any(p => p.Button && !p.Glyph && p.Sprite != null && p.SourcePart == "MINBUTTON")) ReportSharedXpFaces(window, names);
            report.Notes.Add("XP has no composited DWM atlas: frame and caption-button artwork is synthesized at the template's sprite dimensions. Native compositor caption/button placement, maximized frame selection and shadows remain template/OS behavior; this is not verified native 1:1 rendering.");
        }

        private void BuildXpButton(XpFramePlan plan, StyleClass window, int sourcePart, int targetCount)
        {
            if (targetCount != 4 || plan.Rect.Width < 3 || plan.Rect.Height / targetCount < 3)
            {
                plan.Note = "target button is not a nonempty normal/hot/pressed/disabled strip";
                return;
            }
            var sprite = new ArgbImage(plan.Rect.Width, plan.Rect.Height);
            var margins = new Margins[targetCount];
            var content = new Margins[targetCount];
            bool inactive = plan.Part.Name.IndexOf("INACTIVE", StringComparison.OrdinalIgnoreCase) >= 0;
            bool anyEmbedded = false;
            for (int i = 0; i < targetCount; ++i)
            {
                int state = i + 1 + (inactive ? 4 : 0);
                int count = EffectiveCount(m_imp.Source.Style, window, sourcePart, state);
                int top = i * plan.Rect.Height / targetCount, height = (i + 1) * plan.Rect.Height / targetCount - top;
                int glyphType = SemanticModel.FindEffective(m_imp.Source.Style, window, sourcePart, state, IDENTIFIER.GLYPHTYPE)?.GetValue() is int sourceGlyphType ? sourceGlyphType : 0;
                if (glyphType == 2)
                {
                    plan.Note = "source uses a font glyph, not bitmap artwork; no source pixels exist to transfer into the compositor atlas";
                    return;
                }
                bool imageGlyph = glyphType == 1;
                var glyphProperty = imageGlyph ? SelectXpGlyph(window, sourcePart, state, plan.Part.Name, plan.Rect.Width - 2, height - 2) : null;
                var faceProperty = SemanticModel.FindEffective(m_imp.Source.Style, window, sourcePart, state, IDENTIFIER.IMAGEFILE);
                bool embedded = glyphProperty == null;
                anyEmbedded |= embedded;
                // A single painted XP button has no separate face. Put its complete artwork on
                // the glyph layer, and make the corresponding background transparent.
                var property = plan.Glyph ? glyphProperty ?? faceProperty : embedded ? null : faceProperty;
                if (property == null && !plan.Glyph && embedded)
                {
                    margins[i] = new Margins(0, 0, 0, 0);
                    continue;
                }
                if (!(property?.GetValue() is int imageId)) { plan.Note = "source caption-button picture is absent"; return; }
                if (KeyPattern.Matches(m_imp.Options.Exclude, "Window", plan.SourcePart, SemanticModel.PropertyName(property.Header.nameID)))
                {
                    plan.Note = "source caption-button artwork excluded by rule";
                    return;
                }
                var source = m_imp.Decoded(m_imp.Source, "IMAGE", imageId & 0xFFFF);
                bool horizontal = SemanticModel.FindEffective(m_imp.Source.Style, window, sourcePart, state, IDENTIFIER.IMAGELAYOUT)?.GetValue() is int layout ? layout == 1 : true;
                var frame = CutXpButtonFrame(source, count, horizontal, i, inactive);
                if (frame == null) { plan.Note = "source caption-button strip does not describe 1, 4 or 8 semantic states"; return; }
                ApplyXpColorKey(frame, window, sourcePart, state, plan.Glyph && !embedded);
                ArgbImage rendered;
                if (plan.Glyph)
                {
                    rendered = PadXpGlyph(frame, plan.Rect.Width, height);
                    margins[i] = new Margins(0, 0, 0, 0);
                }
                else
                {
                    bool tile = false; // see the frame path: uDWM stretches, no part carries SIZINGTYPE
                    rendered = SynthesizeXpFrameStrip(frame, 1, false, plan.Rect.Width, height, 1,
                        EffectiveSizing(m_imp.Source.Style, window, sourcePart, state), tile, out margins[i]);
                }
                sprite.Blit(rendered, 0, top);
                content[i] = SemanticModel.FindEffective(m_imp.Source.Style, window, sourcePart, state, IDENTIFIER.CONTENTMARGINS)?.GetValue() is Margins cm ? cm : null;
            }
            plan.Sprite = sprite;
            plan.Margins = margins;
            plan.ContentMargins = content;
            plan.Note = plan.Glyph
                ? (anyEmbedded ? "complete single-layer XP button fitted to the target glyph slot; source has no separate glyph layer" : "XP glyph size selected from its own image set, centered into target glyph slot; shared DWM glyph strip uses XP active states")
                : (anyEmbedded ? "transparent background for a single-layer XP button carried by its glyph" : "XP button face: normal/hot/pressed/disabled, " + (inactive ? "inactive states 5-8" : "active states 1-4") + "; source nine-grid margins retained where they fit");
            if (plan.Glyph) ReportInactiveGlyphDifference(window, sourcePart, plan.SourcePart);
        }

        private StyleProperty SelectXpGlyph(StyleClass window, int part, int state, string targetName, int width, int height)
        {
            int select = SemanticModel.FindEffective(m_imp.Source.Style, window, part, state, IDENTIFIER.IMAGESELECTTYPE)?.GetValue() is int s ? s : 0;
            if (select == 0) return SemanticModel.FindEffective(m_imp.Source.Style, window, part, state, IDENTIFIER.GLYPHIMAGEFILE);
            int dpi = targetName.Contains("192") ? 192 : targetName.Contains("144") ? 144 : targetName.Contains("120") ? 120 : 96;
            StyleProperty selected = null;
            int best = -1;
            for (int i = 0; i < 5; ++i)
            {
                var property = SemanticModel.FindEffective(m_imp.Source.Style, window, part, state, (IDENTIFIER)((int)IDENTIFIER.IMAGEFILE1 + i));
                if (!(property?.GetValue() is int id)) continue;
                var picture = m_imp.Decoded(m_imp.Source, "IMAGE", id & 0xFFFF);
                if (picture == null) continue;
                int count = EffectiveCount(m_imp.Source.Style, window, part, state);
                bool horizontal = SemanticModel.FindEffective(m_imp.Source.Style, window, part, state, IDENTIFIER.IMAGELAYOUT)?.GetValue() is int layout ? layout == 1 : true;
                int w = picture.Width / (horizontal ? count : 1), h = picture.Height / (horizontal ? 1 : count);
                if (w < 1 || h < 1) continue;
                if (selected == null) selected = property; // XP falls back to its first available size.
                if (select == 2)
                {
                    int threshold = SemanticModel.FindEffective(m_imp.Source.Style, window, part, state, (IDENTIFIER)((int)IDENTIFIER.MINDPI1 + i))?.GetValue() is int d ? d : 96;
                    if (threshold <= dpi && threshold >= best) { best = threshold; selected = property; }
                }
                else
                {
                    var minimum = SemanticModel.FindEffective(m_imp.Source.Style, window, part, state, (IDENTIFIER)((int)IDENTIFIER.MINSIZE1 + i))?.GetValue();
                    int mw = minimum is Size size ? size.Width : w, mh = minimum is Size size2 ? size2.Height : h;
                    int area = w * h;
                    if (mw <= width && mh <= height && area >= best) { best = area; selected = property; }
                }
            }
            return selected;
        }

        internal static ArgbImage CutXpButtonFrame(ArgbImage source, int count, bool horizontal, int stateIndex, bool inactive)
        {
            if (source == null || stateIndex < 0 || stateIndex >= 4) return null;
            int index;
            if (count == 1) index = 0;
            else if (count == 4) index = stateIndex;                          // N,H,P,D -- no inactive half
            else if (count == 8) index = stateIndex + (inactive ? 4 : 0);      // N,H,P,D then N,H,P,D
            else if (count == 3)
            {
                // three frames and no inactive half at all: NORMAL/HOT/PUSHED only. The inactive and disabled
                // states draw the matching active frame (a pack that ships no inactive art has nothing better).
                if (stateIndex == 3) stateIndex = 0;
                index = stateIndex;
            }
            else if (count == 6)
            {
                // Elfin2/Wood ship three states per mode with the inactive half mirrored: N,H,P then P,H,N.
                // Measured on Wood's 21x126 strip: the pairs (1,6),(2,5),(3,4) are the closest matches
                // (~30 vs ~54 for the unmirrored reading), and frames 3/4 are the only sunken (pressed) ones.
                // There is no DISABLED frame, so stateIndex 3 falls back to NORMAL.
                if (stateIndex == 3) stateIndex = 0;
                index = inactive ? 5 - stateIndex : stateIndex;
            }
            else return null;
            int w = source.Width / (horizontal ? count : 1), h = source.Height / (horizontal ? 1 : count);
            return w < 1 || h < 1 ? null : source.Crop(new Rectangle(horizontal ? index * w : 0, horizontal ? 0 : index * h, w, h));
        }

        internal static ArgbImage PadXpGlyph(ArgbImage glyph, int width, int height)
        {
            if (glyph == null || glyph.Width < 1 || glyph.Height < 1 || width < 3 || height < 3) return null;
            int roomW = width - 2, roomH = height - 2;
            int w = glyph.Width, h = glyph.Height;
            if (w > roomW || h > roomH)
            {
                // Never stretch glyphs up: XP chooses native glyph sizes. Only shrink a glyph
                // when the target atlas slot cannot contain the smallest authored size.
                if ((long)w * roomH > (long)h * roomW) { h = Math.Max(1, (int)((long)h * roomW / w)); w = roomW; }
                else { w = Math.Max(1, (int)((long)w * roomH / h)); h = roomH; }
            }
            int left = 1 + (roomW - w) / 2, top = 1 + (roomH - h) / 2;
            var output = new ArgbImage(width, height);
            for (int y = 0; y < h; ++y)
                for (int x = 0; x < w; ++x)
                    output.Pixels[(top + y) * width + left + x] = glyph.Pixels[(int)((long)y * glyph.Height / h) * glyph.Width + (int)((long)x * glyph.Width / w)];
            // Duplicate edges of the interior, as for frame sprites, without introducing a
            // second glyph or an opaque colored fringe in the compositor's inset pixels.
            for (int y = 1; y < height - 1; ++y) { output.Pixels[y * width] = output.Pixels[y * width + 1]; output.Pixels[y * width + width - 1] = output.Pixels[y * width + width - 2]; }
            Array.Copy(output.Pixels, width, output.Pixels, 0, width);
            Array.Copy(output.Pixels, (height - 2) * width, output.Pixels, (height - 1) * width, width);
            return output;
        }

        private readonly HashSet<string> m_inactiveGlyphNotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void ReportInactiveGlyphDifference(StyleClass window, int part, string name)
        {
            if (!m_inactiveGlyphNotes.Add(name)) return;
            for (int i = 0; i < 4; ++i)
            {
                var property = SelectXpGlyph(window, part, i + 1, "96", 4096, 4096)
                    ?? SemanticModel.FindEffective(m_imp.Source.Style, window, part, i + 1, IDENTIFIER.IMAGEFILE);
                if (!(property?.GetValue() is int id)) continue;
                var picture = m_imp.Decoded(m_imp.Source, "IMAGE", id & 0xFFFF);
                int count = EffectiveCount(m_imp.Source.Style, window, part, i + 1);
                bool horizontal = SemanticModel.FindEffective(m_imp.Source.Style, window, part, i + 1, IDENTIFIER.IMAGELAYOUT)?.GetValue() is int layout ? layout == 1 : true;
                var active = CutXpButtonFrame(picture, count, horizontal, i, false);
                var inactive = CutXpButtonFrame(picture, count, horizontal, i, true);
                if (active == null || inactive == null || Win7StyleImporter.NearlyEqual(active, inactive, 0)) continue;
                m_imp.Report.Notes.Add($"DWMWindow Window|{name}: XP inactive glyph pixels differ from active pixels; the target exposes one shared four-state glyph strip, so it carries active glyphs while separate inactive background parts carry inactive faces.");
                return;
            }
        }

        private void ReportSharedXpFaces(StyleClass window, IReadOnlyDictionary<int, string> names)
        {
            int sharedPart = names.Where(n => n.Value == "MINBUTTON").Select(n => n.Key).DefaultIfEmpty(-1).First();
            if (sharedPart < 0) return;
            foreach (var other in names.Where(n => n.Value == "MAXBUTTON" || n.Value == "RESTOREBUTTON" || n.Value == "HELPBUTTON"))
            {
                if (!window.Parts.ContainsKey(other.Key)) continue;
                for (int state = 1; state <= 8; ++state)
                {
                    var shared = XpFaceFrame(window, sharedPart, state);
                    var picture = XpFaceFrame(window, other.Key, state);
                    if (shared == null || picture == null || Win7StyleImporter.NearlyEqual(shared, picture, 0)) continue;
                    m_imp.Report.Notes.Add($"DWMWindow Window|{other.Value}: source button face differs from MINBUTTON in state {state}; the target exposes one shared caption-button background, so its face uses MINBUTTON while its own glyph is imported separately.");
                    break;
                }
            }
        }

        private ArgbImage XpFaceFrame(StyleClass window, int part, int state)
        {
            var property = SemanticModel.FindEffective(m_imp.Source.Style, window, part, state, IDENTIFIER.IMAGEFILE);
            if (!(property?.GetValue() is int id)) return null;
            int count = EffectiveCount(m_imp.Source.Style, window, part, state);
            bool horizontal = SemanticModel.FindEffective(m_imp.Source.Style, window, part, state, IDENTIFIER.IMAGELAYOUT)?.GetValue() is int layout ? layout == 1 : true;
            return CutXpButtonFrame(m_imp.Decoded(m_imp.Source, "IMAGE", id & 0xFFFF), count, horizontal, (state - 1) % 4, state > 4);
        }

        private void ApplyXpColorKey(ArgbImage frame, StyleClass window, int part, int state, bool glyph = false)
        {
            if (!(SemanticModel.FindEffective(m_imp.Source.Style, window, part, state, glyph ? IDENTIFIER.GLYPHTRANSPARENT : IDENTIFIER.TRANSPARENT_)?.GetValue() is bool transparent) || !transparent) return;
            // Alpha art already carries transparency; XP's color key applies to opaque bitmaps.
            if (frame.Pixels.Any(p => (p >> 24) != 255)) return;
            var key = SemanticModel.FindEffective(m_imp.Source.Style, window, part, state, glyph ? IDENTIFIER.GLYPHTRANSPARENTCOLOR : IDENTIFIER.TRANSPARENTCOLOR)?.GetValue() is Color c ? c : Color.Magenta;
            uint rgb = (uint)key.ToArgb() & 0xFFFFFF;
            for (int i = 0; i < frame.Pixels.Length; ++i)
                if ((frame.Pixels[i] & 0xFFFFFF) == rgb) frame.Pixels[i] = 0;
        }

        /// <summary>
        /// A system colour as XP resolved it for this style: the <c>sysmetrics</c> record, else uxtheme's
        /// <c>DefaultColors</c> (loader.cpp:1302). XP's caption text is not a Window record at all —
        /// <c>nctheme.cpp:2512</c> <c>_GetNcCaptionTextColor</c> returns <c>GetSysColor(COLOR_CAPTIONTEXT)</c> and
        /// <c>loader.cpp:1173</c> fills the system colours from the style's sysmetrics class — so a source that
        /// recolored its titlebar text states it here and nowhere else.
        /// </summary>
        private Color? XpSysColor(IDENTIFIER id)
        {
            if (m_imp.Source.ClassesByName.TryGetValue("sysmetrics", out var sm)
                && SemanticModel.FindEffective(m_imp.Source.Style, sm, 0, 0, id)?.GetValue() is Color c)
                return c;
            switch (id)
            {
                case IDENTIFIER.CAPTIONTEXT: return Color.FromArgb(255, 255, 255);
                case IDENTIFIER.INACTIVECAPTIONTEXT: return Color.FromArgb(212, 208, 200);
                default: return null;
            }
        }

        private void PutXpProperty(StyleClass cls, PartData part, int state, IDENTIFIER name, IDENTIFIER type, object value)
        {
            string propertyName = SemanticModel.PropertyName((int)name);
            if (KeyPattern.Matches(m_imp.Options.Exclude, Win7StyleImporter.DwmClass, part.Name, propertyName)) return;
            var targetPart = cls.Parts[part.Id];
            if (!targetPart.States.TryGetValue(state, out var targetState))
            {
                targetState = new StyleState { StateId = state, StateName = "State " + state };
                targetPart.States.Add(state, targetState);
            }
            var properties = targetState.Properties.Where(p => p.Header.nameID == (int)name && p.Header.typeID == (int)type).ToList();
            if (properties.Count == 0)
            {
                var property = new StyleProperty(new PropertyHeader((int)name, (int)type) { classID = cls.ClassId, partID = part.Id, stateID = state });
                targetState.Properties.Add(property);
                properties.Add(property);
                m_imp.AddedRecords.Add($"{cls.ClassId}/{part.Id}/{state}/{(int)name}/{(int)type}");
            }
            foreach (var property in properties)
            {
                if (Equals(property.GetValue(), value)) continue;
                property.SetValue(value);
                m_imp.MarkTouched(property, $"DWMWindow|{part.Name}|S{state}|{propertyName}");
                m_imp.Report.Decisions.Add(new MergeDecision { Class = Win7StyleImporter.DwmClass, Part = part.Name, State = state,
                    Property = propertyName, Action = "applied-xp-frame-property", Reason = "describes the synthesized XP frame rather than the template's glass sprite", Result = SemanticModel.FormatValue(property) });
                m_imp.Report.Count("applied-xp-frame-property");
            }
        }

        /// <summary>Rasterizes XP frame states into unchanged DWM packing. Each vertical target
        /// frame includes a duplicated 1px border consumed by uDWM; margins describe its interior.
        /// Integer division matches XP's strip cut (unused remainder rows/columns stay unused).
        /// </summary>
        internal static ArgbImage SynthesizeXpFrameStrip(ArgbImage source, int sourceCount, bool sourceHorizontal,
            int targetWidth, int targetHeight, int targetCount, Margins sourceMargins, bool tile, out Margins outputMargins)
        {
            outputMargins = null;
            if (source == null || sourceCount < 1 || sourceCount > 2 || targetCount < 1 || targetCount > 2
                || targetWidth < 3 || targetHeight / targetCount < 3) return null;
            int sw = source.Width / (sourceHorizontal ? sourceCount : 1), sh = source.Height / (sourceHorizontal ? 1 : sourceCount);
            if (sw < 1 || sh < 1) return null;
            var sm = FitXpMargins(sourceMargins, sw, sh);
            outputMargins = FitXpMargins(sm, targetWidth - 2, targetHeight / targetCount - 2);
            var result = new ArgbImage(targetWidth, targetHeight);
            for (int frame = 0; frame < targetCount; ++frame)
            {
                int index = Math.Min(frame, sourceCount - 1);
                int sx = sourceHorizontal ? index * sw : 0, sy = sourceHorizontal ? 0 : index * sh;
                int top = frame * targetHeight / targetCount, bottom = (frame + 1) * targetHeight / targetCount;
                int dw = targetWidth - 2, dh = bottom - top - 2;
                for (int y = 0; y < bottom - top; ++y)
                {
                    int iy = Math.Max(0, Math.Min(dh - 1, y - 1));
                    int py = MapXpAxis(iy, sh, sm.Top, sm.Bottom, dh, outputMargins.Top, outputMargins.Bottom, tile);
                    for (int x = 0; x < targetWidth; ++x)
                    {
                        int ix = Math.Max(0, Math.Min(dw - 1, x - 1));
                        int px = MapXpAxis(ix, sw, sm.Left, sm.Right, dw, outputMargins.Left, outputMargins.Right, tile);
                        result.Pixels[(top + y) * targetWidth + x] = source.Pixels[(sy + py) * source.Width + sx + px];
                    }
                }
            }
            return result;
        }

        private static Margins FitXpMargins(Margins margins, int width, int height)
        {
            FitXpAxis(Math.Max(0, margins?.Left ?? 0), Math.Max(0, margins?.Right ?? 0), width, out int l, out int r);
            FitXpAxis(Math.Max(0, margins?.Top ?? 0), Math.Max(0, margins?.Bottom ?? 0), height, out int t, out int b);
            return new Margins(l, t, r, b);
        }

        private static void FitXpAxis(int first, int second, int extent, out int a, out int b)
        {
            long sum = (long)first + second;
            if (sum < extent) { a = first; b = second; return; }
            int room = Math.Max(0, extent - 1);
            a = sum == 0 ? 0 : (int)(first * (long)room / sum);
            b = room - a;
        }

        private static int MapXpAxis(int position, int sourceExtent, int sourceFirst, int sourceLast,
            int targetExtent, int targetFirst, int targetLast, bool tile)
        {
            if (position < targetFirst) return (int)((long)position * sourceFirst / targetFirst);
            if (position >= targetExtent - targetLast)
                return sourceExtent - sourceLast + (int)((long)(position - targetExtent + targetLast) * sourceLast / targetLast);
            int sourceBody = sourceExtent - sourceFirst - sourceLast;
            int p = position - targetFirst;
            return sourceFirst + (tile ? p % sourceBody : (int)((long)p * sourceBody / (targetExtent - targetFirst - targetLast)));
        }

        /// <summary>
        /// win7's frame highlights have no win10 consumer, and their win7 ids (34, 43) belong to other
        /// parts on win10, so they're kept under ids past everything uDWM asks for: 92-94 already carry
        /// the button glows for DWM mods, 95/96 carry the highlights. OpenGlass reads them there when its
        /// highlight style is "From msstyles" (its LEFTHIGHLIGHTCORNERid / SIDEHIGHLIGHTid defaults).
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, int> HighlightParts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["LEFTHIGHLIGHTCORNER"] = 95,
            ["SIDEHIGHLIGHT"] = 96,
        };

        /// <summary>
        /// Copies the source's highlight parts into the template's DWMWindow under the ids above. The
        /// output atlas starts with the source atlas at 0,0, so the source rects stay valid as they are.
        /// A template that already uses one of those ids keeps it; that part is skipped with a warning.
        /// </summary>
        private List<string> PreserveHighlights(Dictionary<string, PartData> s, ArgbImage sAtlas)
        {
            var report = m_imp.Report;
            var kept = new List<string>();
            var cls = m_imp.Target.Style.Classes.Values.FirstOrDefault(c => string.Equals(c.ClassName, Win7StyleImporter.DwmClass, StringComparison.OrdinalIgnoreCase));
            if (cls == null) return kept;
            foreach (var kv in HighlightParts)
            {
                if (!s.TryGetValue(kv.Key, out var sp)) continue;
                if (KeyPattern.Matches(m_imp.Options.Exclude, Win7StyleImporter.DwmClass, kv.Key, "*")) continue;
                if (cls.Parts.ContainsKey(kv.Value))
                {
                    report.Warnings.Add($"DWMWindow {kv.Key}: the template already has a part {kv.Value}, so the win7 highlight isn't kept there.");
                    continue;
                }
                var rect = sp.Rects.FirstOrDefault(e => e.StateId == 0);
                if (rect == null) continue;
                var r = RectOf(rect);
                if (r.Width <= 0 || r.Height <= 0 || r.Right > sAtlas.Width || r.Bottom > sAtlas.Height) continue;

                var part = new StylePart { PartId = kv.Value, PartName = "Part " + kv.Value };
                cls.Parts.Add(kv.Value, part);
                foreach (var se in sp.Entries.OrderBy(e => e.StateId))
                {
                    if (!part.States.TryGetValue(se.StateId, out var st))
                    {
                        st = new StyleState { StateId = se.StateId, StateName = "State " + se.StateId };
                        part.States.Add(se.StateId, st);
                    }
                    var prop = new StyleProperty((PropertyHeader)se.Property.Header.Clone());
                    prop.Header.classID = cls.ClassId;
                    prop.Header.partID = kv.Value;
                    Win7StyleImporter.CopyValue(se.Property, prop);
                    st.Properties.Add(prop);
                    m_imp.MarkTouched(prop, $"DWMWindow|Part {kv.Value}|S{se.StateId}|{se.PropertyName}");
                    m_imp.AddedRecords.Add($"{cls.ClassId}/{kv.Value}/{se.StateId}/{prop.Header.nameID}/{prop.Header.typeID}");
                }
                m_imp.SpriteChecks.Add(new SpriteExpectation { Part = "Part " + kv.Value, OutputRect = r, Expected = sAtlas.Crop(r) });
                m_rep.Parts.Add(new DwmPartMapping
                {
                    TargetPartId = kv.Value,
                    TargetPart = "Part " + kv.Value,
                    SourcePart = kv.Key,
                    Method = "kept-win7-part",
                    NewRect = r.ToString(),
                    Note = "win7-only part kept for DWM mods (OpenGlass highlight style \"From msstyles\")"
                });
                report.Count("dwm-highlight-kept");
                kept.Add(kv.Key);
            }
            return kept;
        }

        private void ResolveSource(RectPlan plan, Dictionary<string, PartData> b, Dictionary<string, PartData> s, ArgbImage sAtlas)
        {
            int state = plan.Entry.StateId;
            if (KeyPattern.Matches(m_imp.Options.Exclude, Win7StyleImporter.DwmClass, plan.Part.Name, "ATLASRECT"))
            {
                plan.Method = "fallback";
                plan.Note = "excluded by rule, template sprite kept";
                return;
            }
            if (b.TryGetValue(plan.Part.Name, out var bp))
            {
                var br = bp.Rects.FirstOrDefault(r => r.StateId == state);
                if (br != null && RectOf(br) == plan.TargetRect)
                {
                    plan.Src = bp.Name;
                    plan.Method = "name";
                }
            }

            if (plan.Src == null)
            {
                var candidates = new List<string>();
                foreach (var bp2 in b.Values)
                    foreach (var r in bp2.Rects)
                        if (r.StateId == state && RectOf(r) == plan.TargetRect) candidates.Add(bp2.Name);

                var withSource = candidates.Where(n => s.TryGetValue(n, out var sp) && sp.Rects.Any(r => r.StateId == state)).OrderBy(n => n).ToList();
                if (withSource.Count > 0)
                {
                    string chosen = ChooseAlias(withSource, n => SafeCrop(sAtlas, RectOf(s[n].Rects.First(r => r.StateId == state))));
                    if (chosen != null)
                    {
                        plan.Src = chosen;
                        plan.Method = "alias";
                        plan.Note = (b.ContainsKey(plan.Part.Name) ? "template moved this part onto the sprite of " : "win10-only part reuses the stock sprite of ")
                            + string.Join("/", withSource);
                        if (b.ContainsKey(plan.Part.Name))
                        {
                            // e.g. aero11 points SMALLBUTTONCLOSEGLYPH192 at the restore glyph. kept, since the
                            // template is the authority, but it smells like a template bug worth a look.
                            m_imp.Report.Warnings.Add($"DWMWindow {plan.Part.Name}: template uses the stock sprite of {string.Join("/", withSource)} instead of its own; the output keeps that pairing with the source's {withSource[0]} sprite.");
                        }
                    }
                    else
                    {
                        plan.Method = "fallback";
                        plan.Note = "ambiguous: stock rect shared by " + string.Join("/", withSource) + " and the source gave them different sprites";
                        return;
                    }
                }
            }

            if (plan.Src == null)
            {
                plan.Method = "fallback";
                plan.Note = "no win7 part owns this rect in the stock atlas";
                return;
            }

            var srcRect = s.TryGetValue(plan.Src, out var spart) ? spart.Rects.FirstOrDefault(r => r.StateId == state) : null;
            if (srcRect == null)
            {
                plan.Method = "fallback";
                plan.Note = "source has no ATLASRECT for " + plan.Src;
                plan.Src = null;
                return;
            }
            plan.SourceRect = RectOf(srcRect);
            if (SafeCrop(sAtlas, plan.SourceRect) == null || plan.SourceRect.Width <= 0 || plan.SourceRect.Height <= 0)
            {
                plan.Method = "fallback";
                plan.Note = $"source rect {plan.SourceRect} is outside the source atlas";
                plan.Src = null;
            }
        }

        private static bool InsideAtlas(ArgbImage image, Rectangle rect)
        {
            return rect.X >= 0 && rect.Y >= 0 && rect.Width > 0 && rect.Height > 0 && rect.Right <= image.Width && rect.Bottom <= image.Height;
        }

        private static ArgbImage SafeCrop(ArgbImage img, Rectangle r)
        {
            if (r.X < 0 || r.Y < 0 || r.Right > img.Width || r.Bottom > img.Height || r.Width < 0 || r.Height < 0) return null;
            return img.Crop(r);
        }

        /// <summary>
        /// Several win7 parts can share one stock sprite. Following the shared rect is only safe when
        /// the source also gave all of them the same pixels; otherwise there's no telling which one
        /// the template meant. Returns the first name (ordinal order) or null when ambiguous.
        /// </summary>
        internal static string ChooseAlias(IList<string> candidates, Func<string, ArgbImage> sourceSprite)
        {
            var ordered = candidates.OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (ordered.Count == 0) return null;
            var sprites = ordered.Select(sourceSprite).ToList();
            if (sprites.Any(x => x == null)) return null;
            return sprites.Skip(1).All(x => Win7StyleImporter.NearlyEqual(x, sprites[0], 0)) ? ordered[0] : null;
        }

        // what describes the pixels of an atlas sprite: frame count, nine-grid and content padding.
        private static readonly HashSet<int> SpriteGeometry = new HashSet<int>
        {
            (int)IDENTIFIER.IMAGECOUNT, (int)IDENTIFIER.SIZINGMARGINS, (int)IDENTIFIER.CONTENTMARGINS,
        };

        /// <param name="spriteFromSource">the part's ATLASRECT now points at the source's sprite.</param>
        private void MergePart(PartData tp, PartData bp, PartData sp, string src, bool spriteFromSource)
        {
            string label = tp.Name + (src != tp.Name ? " (from " + src + ")" : "");
            foreach (var te in tp.Entries)
            {
                if (te.NameId == (int)IDENTIFIER.ATLASRECT && te.TypeId == (int)IDENTIFIER.RECTTYPE) continue;
                if (te.TypeId == (int)IDENTIFIER.DISKSTREAM) continue;
                var be = bp.Find(te.StateId, te.PropertyName, te.TypeId, te.Occurrence);
                var se = sp.Find(te.StateId, te.PropertyName, te.TypeId, te.Occurrence);
                if (be == null && se == null) continue;

                // the sprite comes with the numbers that describe it, like replacing a part in the
                // editor: even where the template adapted them, they belonged to the template's sprite.
                if (spriteFromSource && se != null && SpriteGeometry.Contains(te.NameId) && !m_imp.Same(m_imp.Source, se, m_imp.Target, te))
                {
                    if (m_imp.ApplySourceValue(m_imp.Source, se, te.Property, out string err))
                    {
                        m_imp.MarkTouched(te.Property, $"{te.ClassName}|{te.PartName}|S{te.StateId}|{te.PropertyName}");
                        m_imp.Decide(te, "applied-with-sprite", "describes the source sprite this part now uses", be, se, te, SemanticModel.FormatValue(te.Property));
                        m_imp.Report.Count("applied-with-sprite");
                    }
                    else m_imp.Report.Warnings.Add($"DWMWindow {tp.Name}: {te.PropertyName} not applied, {err}");
                    continue;
                }
                m_imp.ThreeWay(Win7StyleImporter.DwmClass, label, te.StateId, te.PropertyName, be, se, te, m_imp.Source, true);
            }
            foreach (var se in sp.Entries)
            {
                if (se.NameId == (int)IDENTIFIER.ATLASRECT && se.TypeId == (int)IDENTIFIER.RECTTYPE) continue;
                if (se.TypeId == (int)IDENTIFIER.DISKSTREAM) continue;
                if (tp.Find(se.StateId, se.PropertyName, se.TypeId, se.Occurrence) != null) continue;
                var be = bp.Find(se.StateId, se.PropertyName, se.TypeId, se.Occurrence);
                m_imp.ThreeWay(Win7StyleImporter.DwmClass, tp.Name, se.StateId, se.PropertyName, be, se, null, m_imp.Source, true);
            }
        }

        private bool GeometryFits(PartData tp, int state, Rectangle rect, out string why)
        {
            var style = m_imp.Target.Style;
            var cls = style.Classes[tp.Entries[0].ClassId];
            return CheckGeometry(rect, EffectiveCount(style, cls, tp.Id, state), EffectiveSizing(style, cls, tp.Id, state), out why);
        }

        internal static int EffectiveCount(VisualStyle style, StyleClass cls, int part, int state)
        {
            return SemanticModel.FindEffective(style, cls, part, state, IDENTIFIER.IMAGECOUNT)?.GetValue() is int n && n > 0 ? n : 1;
        }

        internal static Margins EffectiveSizing(VisualStyle style, StyleClass cls, int part, int state)
        {
            return SemanticModel.FindEffective(style, cls, part, state, IDENTIFIER.SIZINGMARGINS)?.GetValue() as Margins;
        }

        /// <summary>
        /// uDWM takes the atlas rect, drops a 1px border on every side and cuts it into IMAGECOUNT
        /// vertical strips (4 for buttons/glyphs, 2 for frames), each nine-gridded with SIZINGMARGINS.
        /// Strips are taken as sliced proportionally, like the editor does (129px in 2 = 64 + 65; how
        /// uDWM rounds isn't verified), so only a strip left empty by the inset or margins wider than
        /// the smallest strip count as a problem.
        /// </summary>
        internal static bool CheckGeometry(Rectangle rect, int imageCount, Margins sizing, out string why)
        {
            why = null;
            if (imageCount < 1) imageCount = 1;
            int fw = rect.Width - 2, fh = rect.Height / imageCount - 2;
            if (fw < 1 || fh < 1)
            {
                why = $"rect {rect.Width}x{rect.Height} leaves empty {imageCount}-frame strips after the 1px inset";
                return false;
            }
            if (sizing != null && (sizing.Left + sizing.Right > fw || sizing.Top + sizing.Bottom > fh))
            {
                why = $"sizing margins l{sizing.Left} r{sizing.Right} t{sizing.Top} b{sizing.Bottom} don't fit a {fw}x{fh} frame";
                return false;
            }
            return true;
        }

        private void BuildAtlas(List<RectPlan> plans, SemanticEntry ta, SemanticEntry sa, ArgbImage tAtlas, ArgbImage sAtlas)
        {
            var report = m_imp.Report;
            var fallbacks = plans.Where(p => p.Method == "fallback").ToList();

            // shelf-pack the kept template sprites under the source atlas. identical rects share a slot.
            var slots = new Dictionary<Rectangle, Rectangle>();
            int width = sAtlas.Width, y = sAtlas.Height, x = 0, rowH = 0;
            foreach (var fr in fallbacks.Select(p => p.TargetRect).Distinct().OrderByDescending(r => r.Height))
            {
                width = Math.Max(width, fr.Width);
                if (x + fr.Width > width) { y += rowH; x = 0; rowH = 0; }
                slots[fr] = new Rectangle(x, y, fr.Width, fr.Height);
                x += fr.Width;
                rowH = Math.Max(rowH, fr.Height);
            }
            int height = y + rowH;

            byte[] atlasBytes;
            ArgbImage outAtlas;
            if (slots.Count == 0)
            {
                // nothing to add: take the source atlas bytes as they are, no recompression.
                atlasBytes = m_imp.Source.GetResourceBytes("STREAM", sa.ResourceId);
                outAtlas = sAtlas;
            }
            else
            {
                outAtlas = new ArgbImage(width, height);
                outAtlas.Blit(sAtlas, 0, 0);
                foreach (var kv in slots) outAtlas.Blit(tAtlas.Crop(kv.Key), kv.Value.X, kv.Value.Y);
                atlasBytes = ImageIdentity.EncodePngVerified(outAtlas);
                m_rep.AtlasReencoded = true;
            }
            m_rep.NewAtlasSize = $"{outAtlas.Width}x{outAtlas.Height}";

            bool atlasChanged = !Win7StyleImporter.NearlyEqual(outAtlas, tAtlas, 0);
            int atlasId = ta.ResourceId;
            if (atlasChanged)
            {
                var info = ImageIdentity.Analyze(atlasBytes);
                atlasId = m_imp.PlaceResource("STREAM", sa.ResourceId, atlasBytes, info);
                ta.Property.SetValue(atlasId);
                m_imp.MarkTouched(ta.Property, $"{ta.ClassName}|{ta.PartName}|S{ta.StateId}|{ta.PropertyName}");
                m_imp.Decide(ta, "applied", "DWM atlas replaced by the source atlas" + (slots.Count > 0 ? $" plus {slots.Count} kept template sprites" : ""), null, null, ta, atlasId.ToString());
            }
            m_rep.NewAtlasId = atlasId;

            foreach (var p in plans)
            {
                var map = new DwmPartMapping
                {
                    TargetPartId = p.Part.Id,
                    TargetPart = p.Part.Name + (p.Entry.StateId != 0 ? " S" + p.Entry.StateId : ""),
                    SourcePart = p.Src,
                    Method = p.Method,
                    TargetRect = p.TargetRect.ToString(),
                    Note = p.Note
                };
                m_rep.Parts.Add(map);
                if (p.Method == "none") continue;

                Rectangle newRect;
                ArgbImage expected;
                if (p.Method == "fallback")
                {
                    newRect = slots[p.TargetRect];
                    expected = tAtlas.Crop(p.TargetRect);
                    // one entry per part: a part with two falling-back states is still one kept part.
                    if (!m_rep.FallbackParts.Contains(p.Part.Name)) m_rep.FallbackParts.Add(p.Part.Name);
                }
                else
                {
                    newRect = p.SourceRect;
                    expected = sAtlas.Crop(p.SourceRect);
                }
                map.NewRect = newRect.ToString();
                m_imp.SpriteChecks.Add(new SpriteExpectation { Part = p.Part.Name, OutputRect = newRect, Expected = expected });

                if (newRect != p.TargetRect)
                {
                    p.Entry.Property.SetValue(new Margins(newRect.Left, newRect.Top, newRect.Right, newRect.Bottom));
                    m_imp.MarkTouched(p.Entry.Property, $"DWMWindow|{p.Part.Name}|S{p.Entry.StateId}|ATLASRECT");
                }
                report.Count("dwm-rect-" + p.Method);
            }
        }
    }
}
