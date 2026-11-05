using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace libmsstyle.Import
{
    /// <summary>
    /// Win10 xp ports re-cut some xp strips into more frames, because win10 added states
    /// (Toolbar BUTTON 6 -> 8, ScrollBar ARROWBTN 16 -> 20 hover states, Button PUSHBUTTON 5 -> 6).
    /// The port author filled the new frames with copies of existing xp frames. Matching the
    /// template's frames against stock xp luna's frames by pixels recovers that choice per
    /// class/part/state, and the source's strip is re-cut the same way. The base gets the same
    /// treatment, so afterwards base, source and template describe the strip with the same frame
    /// count and layout and the regular three-way merge applies.
    ///
    /// A state where some template frame has no matching stock frame (redrawn art, other frame
    /// sizes everywhere) isn't re-cut; the merge then keeps the template's strip for it
    /// (<see cref="Win7StyleImporter"/> locks parts with a template-adapted frame count).
    /// </summary>
    internal class XpStripRemap
    {
        // mean absolute difference over the four premultiplied channels of a pixel. The ports'
        // copied frames differ by antialiased corners only (Button PUSHBUTTON ~5); unrelated frames by 40+.
        private const double MaxFrameDiff = 16;

        private readonly VisualStyle m_base, m_source, m_target;
        private readonly ImportReport m_report;

        public XpStripRemap(VisualStyle baseStyle, VisualStyle source, VisualStyle target, ImportReport report)
        {
            m_base = baseStyle;
            m_source = source;
            m_target = target;
            m_report = report;
        }

        private struct Geometry
        {
            public int Count;
            public bool Horizontal;
            public override string ToString() => $"{Count} {(Horizontal ? "horizontal" : "vertical")}";
        }

        private static Geometry GeometryOf(VisualStyle s, StyleClass c, int part, int state)
        {
            var count = SemanticModel.FindEffective(s, c, part, state, IDENTIFIER.IMAGECOUNT);
            var layout = SemanticModel.FindEffective(s, c, part, state, IDENTIFIER.IMAGELAYOUT);
            return new Geometry
            {
                Count = count?.GetValue() is int n && n > 0 ? n : 1,
                Horizontal = layout?.GetValue() is int l && l == 1,
            };
        }

        private static readonly int[] StripIds =
        {
            (int)IDENTIFIER.IMAGEFILE, (int)IDENTIFIER.IMAGEFILE1, (int)IDENTIFIER.IMAGEFILE2, (int)IDENTIFIER.IMAGEFILE3,
            (int)IDENTIFIER.IMAGEFILE4, (int)IDENTIFIER.IMAGEFILE5,
        };

        public void Run()
        {
            var targetByName = m_target.Classes.Values.GroupBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var sourceByName = m_source.Classes.Values.ToDictionary(c => c.ClassName, StringComparer.OrdinalIgnoreCase);

            // a part the template redrew takes the frame map of the part it is drawn on: the gripper lies on the
            // thumb and has the thumb's states. Luna10's grippers (8, 9) are blue art, so without this GSM's and
            // appows' grippers were dropped for Luna's. Class-wide guessing doesn't work: flat track frames match
            // each other by pixels and yield maps (1,1,3,1,1) that don't describe states.
            var mapDonors = new Dictionary<(string, int), int>
            {
                [("ScrollBar", 8)] = 2, // GRIPPERHORZ <- THUMBBTNHORZ
                [("ScrollBar", 9)] = 3, // GRIPPERVERT <- THUMBBTNVERT
            };
            var learned = new Dictionary<(string, int, int), int[]>();
            foreach (var bc in m_base.Classes.Values)
            {
                if (!targetByName.TryGetValue(bc.ClassName, out var tc)) continue;
                foreach (var donor in mapDonors.Where(d => string.Equals(d.Key.Item1, bc.ClassName, StringComparison.OrdinalIgnoreCase)).Select(d => d.Value).Distinct())
                {
                    if (!bc.Parts.TryGetValue(donor, out var bp)) continue;
                    foreach (var bs in bp.States.Values)
                    {
                        var images = bs.Properties.Where(p => StripIds.Contains(p.Header.nameID)).ToList();
                        var m = images.Count == 0 ? null : LearnMap(images, tc, donor, bs.StateId,
                            GeometryOf(m_base, bc, donor, bs.StateId), GeometryOf(m_target, tc, donor, bs.StateId));
                        if (m != null) learned[(bc.ClassName.ToLowerInvariant(), donor, bs.StateId)] = m;
                    }
                }
            }

            foreach (var bc in m_base.Classes.Values.ToList())
            {
                if (!targetByName.TryGetValue(bc.ClassName, out var tc)) continue;
                sourceByName.TryGetValue(bc.ClassName, out var sc);
                foreach (var bp in bc.Parts.Values)
                {
                    foreach (var bs in bp.States.Values)
                    {
                        var images = bs.Properties.Where(p => StripIds.Contains(p.Header.nameID)).ToList();
                        if (images.Count == 0) continue;
                        var bg = GeometryOf(m_base, bc, bp.PartId, bs.StateId);
                        var tg = GeometryOf(m_target, tc, bp.PartId, bs.StateId);
                        if (bg.Count == tg.Count && bg.Horizontal == tg.Horizontal) continue;
                        string label = $"{bc.ClassName} part {bp.PartId} state {bs.StateId}";

                        int[] map = LearnMap(images, tc, bp.PartId, bs.StateId, bg, tg);
                        var donorKey = mapDonors.Keys.FirstOrDefault(k => k.Item2 == bp.PartId && string.Equals(k.Item1, bc.ClassName, StringComparison.OrdinalIgnoreCase));
                        if (map == null && donorKey.Item1 != null
                            && learned.TryGetValue((bc.ClassName.ToLowerInvariant(), mapDonors[donorKey], bs.StateId), out var donorMap) && donorMap.Length == tg.Count)
                        {
                            map = donorMap;
                            m_report.Notes.Add($"{label}: template redrew this strip; it takes part {mapDonors[donorKey]}'s frame map, the part it is drawn on.");
                        }
                        if (map == null)
                        {
                            m_report.Notes.Add($"{label}: template re-cut the strip from {bg} to {tg} frames with art that isn't stock xp; the template's strip stays.");
                            m_report.Count("xp-strip-not-remapped");
                            continue;
                        }

                        // an image glyph is cut with the part's IMAGECOUNT too: it goes along with the same map
                        // (Toolbar SPLITBUTTONDROPDOWN, the back/forward arrows), or its frames stop lining up.
                        Recut(m_base, bc, bp.PartId, bs, WithGlyph(bs, images), bg, tg, map);
                        if (sc != null && sc.Parts.TryGetValue(bp.PartId, out var sp) && sp.States.TryGetValue(bs.StateId, out var ss))
                        {
                            var sg = GeometryOf(m_source, sc, bp.PartId, bs.StateId);
                            var simages = ss.Properties.Where(p => StripIds.Contains(p.Header.nameID)).ToList();
                            if (simages.Count > 0 && sg.Count == bg.Count)
                            {
                                // same frames as stock, maybe laid out the other way (GSM's thumbs are 4 side by side):
                                // frame i means the same state, so the stock map applies to the source's own cut.
                                Recut(m_source, sc, bp.PartId, ss, WithGlyph(ss, simages), sg, tg, map);
                            }
                            else if (simages.Count > 0 && sg.Count == 1)
                            {
                                // one frame draws every state on xp; each template frame shows it.
                                Recut(m_source, sc, bp.PartId, ss, WithGlyph(ss, simages), sg, tg, new int[tg.Count]);
                                m_report.Notes.Add($"{label}: source draws every state with one frame; repeated into the template's {tg} frames.");
                            }
                            else if (simages.Count > 0)
                            {
                                m_report.Warnings.Add($"{label}: source cuts this strip into {sg} frames, stock xp into {bg}; the template's {tg} frame mapping doesn't apply, source strip left as is.");
                            }
                        }
                        m_report.Notes.Add($"{label}: strip re-cut from {bg} to {tg} frames the way the template did it (frames {string.Join(",", map.Select(i => i + 1))}).");
                        m_report.Count("xp-strip-remapped");
                    }
                }
            }
        }

        private static List<StyleProperty> WithGlyph(StyleState state, List<StyleProperty> images)
        {
            var glyph = state.Properties.FirstOrDefault(p => p.Header.nameID == (int)IDENTIFIER.GLYPHIMAGEFILE);
            return glyph == null ? images : images.Concat(new[] { glyph }).ToList();
        }

        /// <summary>
        /// template frame j shows stock frame map[j]. A stock frame is a candidate for j when it matches in every image of
        /// the state that can be compared (background and glyph strips alike). Single images are ambiguous: stock
        /// ScrollBar ARROWBTN backgrounds look the same for every direction, its glyphs the same for normal/hot/pressed;
        /// only both together say that win10's down-hover frame is xp's down frame. Among candidates j itself wins.
        /// </summary>
        private int[] LearnMap(List<StyleProperty> baseImages, StyleClass tc, int part, int state, Geometry bg, Geometry tg)
        {
            HashSet<int>[] candidates = null;
            foreach (var bprop in baseImages)
            {
                var tprop = SemanticModel.FindEffective(m_target, tc, part, state, (IDENTIFIER)bprop.Header.nameID);
                if (tprop == null) continue;
                var bimg = Decode(m_base, bprop);
                var timg = Decode(m_target, tprop);
                if (bimg == null || timg == null) continue;
                var bf = Frames(bimg, bg);
                var tf = Frames(timg, tg);
                if (bf.Length == 0 || tf.Length == 0 || bf[0].Width != tf[0].Width || bf[0].Height != tf[0].Height) continue;

                var sets = new HashSet<int>[tf.Length];
                bool complete = true;
                for (int j = 0; j < tf.Length && complete; ++j)
                {
                    sets[j] = new HashSet<int>(Enumerable.Range(0, bf.Length).Where(i => MeanDiff(bf[i], tf[j]) <= MaxFrameDiff));
                    complete = sets[j].Count > 0;
                }
                if (!complete) continue; // this image isn't a re-cut of stock art
                if (candidates == null) candidates = sets;
                else
                {
                    var narrowed = candidates.Select((c, j) => new HashSet<int>(c.Intersect(sets[j]))).ToArray();
                    if (narrowed.All(c => c.Count > 0)) candidates = narrowed;
                }
            }
            return candidates?.Select((c, j) => c.Contains(j) ? j : c.Min()).ToArray();
        }

        private void Recut(VisualStyle style, StyleClass cls, int part, StyleState state, List<StyleProperty> images, Geometry from, Geometry to, int[] map)
        {
            foreach (var prop in images)
            {
                var img = Decode(style, prop);
                if (img == null) continue;
                var frames = Frames(img, from);
                int w = frames[0].Width, h = frames[0].Height;
                var outImg = new ArgbImage(to.Horizontal ? w * map.Length : w, to.Horizontal ? h : h * map.Length);
                for (int j = 0; j < map.Length; ++j)
                    outImg.Blit(frames[map[j]], to.Horizontal ? j * w : 0, to.Horizontal ? 0 : j * h);
                prop.SetValue(style.AddXpImage(ImageIdentity.EncodePngVerified(outImg)));
            }
            Upsert(state, cls, part, IDENTIFIER.IMAGECOUNT, IDENTIFIER.INT, to.Count);
            Upsert(state, cls, part, IDENTIFIER.IMAGELAYOUT, IDENTIFIER.ENUM, to.Horizontal ? 1 : 0);
        }

        private static void Upsert(StyleState state, StyleClass cls, int part, IDENTIFIER name, IDENTIFIER type, int value)
        {
            var p = state.Properties.FirstOrDefault(x => x.Header.nameID == (int)name);
            if (p == null)
            {
                p = new StyleProperty(new PropertyHeader((int)name, (int)type) { classID = cls.ClassId, partID = part, stateID = state.StateId });
                state.Properties.Add(p);
            }
            p.SetValue(value);
        }

        private static ArgbImage Decode(VisualStyle style, StyleProperty prop)
        {
            var bytes = style.GetRawResource("IMAGE", prop.Header.shortFlag & 0xFFFF);
            if (bytes == null) return null;
            try { return ImageIdentity.Decode(bytes); }
            catch (ArgumentException) { return null; }
        }

        // uxtheme imagefile.cpp: single frame = length / count (integer division), frame i at i * single.
        private static ArgbImage[] Frames(ArgbImage img, Geometry g)
        {
            int w = g.Horizontal ? img.Width / g.Count : img.Width;
            int h = g.Horizontal ? img.Height : img.Height / g.Count;
            if (w <= 0 || h <= 0) return new ArgbImage[0];
            var r = new ArgbImage[g.Count];
            for (int i = 0; i < g.Count; ++i)
                r[i] = img.Crop(new Rectangle(g.Horizontal ? i * w : 0, g.Horizontal ? 0 : i * h, w, h));
            return r;
        }

        private static double MeanDiff(ArgbImage a, ArgbImage b)
        {
            long d = 0;
            for (int i = 0; i < a.Pixels.Length; ++i)
            {
                uint p = a.Pixels[i], q = b.Pixels[i];
                for (int sh = 0; sh < 32; sh += 8) d += Math.Abs((int)((p >> sh) & 0xFF) - (int)((q >> sh) & 0xFF));
            }
            return a.Pixels.Length == 0 ? 0 : d / (double)a.Pixels.Length;
        }
    }
}
