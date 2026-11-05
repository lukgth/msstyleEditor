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
                            GeometryOf(m_base, bc, donor, bs.StateId), GeometryOf(m_target, tc, donor, bs.StateId), new HashSet<int>());
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

                        var guessed = new SortedSet<int>();
                        int[] map = LearnMap(images, tc, bp.PartId, bs.StateId, bg, tg, guessed);
                        if (map != null && guessed.Count > 0)
                            m_report.Notes.Add($"{label}: template frames {string.Join(",", guessed.Select(j => j + 1))} aren't stock xp art (states xp didn't have); they show the closest stock frame.");
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

                        // a source that kept stock's pictures (stock luna itself) takes the same template strips as the
                        // stock cut, or it would "change" every strip it re-cuts. Compared before the stock is re-cut.
                        StyleState ss = null;
                        bool sourceIsStock = sc != null && sc.Parts.TryGetValue(bp.PartId, out var sp) && sp.States.TryGetValue(bs.StateId, out ss)
                            && SamePictures(ss, WithGlyph(bs, images));

                        // an image glyph is cut with the part's IMAGECOUNT too: it goes along with the same map
                        // (Toolbar SPLITBUTTONDROPDOWN, the back/forward arrows), or its frames stop lining up.
                        Recut(m_base, bc, bp.PartId, bs, WithGlyph(bs, images), bg, tg, map, guessed);
                        if (ss != null)
                        {
                            var sg = GeometryOf(m_source, sc, bp.PartId, bs.StateId);
                            var simages = ss.Properties.Where(p => StripIds.Contains(p.Header.nameID)).ToList();
                            if (simages.Count > 0 && sg.Count == bg.Count)
                            {
                                // same frames as stock, maybe laid out the other way (GSM's thumbs are 4 side by side):
                                // frame i means the same state, so the stock map applies to the source's own cut.
                                Recut(m_source, sc, bp.PartId, ss, WithGlyph(ss, simages), sg, tg, map, sourceIsStock ? guessed : null);
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

        /// <summary>true when every base image of the state decodes to the same pixels in the source.</summary>
        private bool SamePictures(StyleState sourceState, List<StyleProperty> baseImages)
        {
            foreach (var bprop in baseImages)
            {
                var sprop = sourceState.Properties.FirstOrDefault(p => p.Header.nameID == bprop.Header.nameID);
                if (sprop == null) return false;
                var a = Decode(m_base, bprop);
                var b = Decode(m_source, sprop);
                if (a == null || b == null || a.PixelSha1() != b.PixelSha1()) return false;
            }
            return true;
        }

        /// <summary>
        /// template frame j shows stock frame map[j]. A stock frame is a candidate for j when it matches in every image of
        /// the state that can be compared (background and glyph strips alike). Single images are ambiguous: stock
        /// ScrollBar ARROWBTN backgrounds look the same for every direction, its glyphs the same for normal/hot/pressed;
        /// only both together say that win10's down-hover frame is xp's down frame. Among candidates j itself wins.
        /// </summary>
        private int[] LearnMap(List<StyleProperty> baseImages, StyleClass tc, int part, int state, Geometry bg, Geometry tg, ISet<int> guessed)
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
                if (bf.Length == 0 || tf.Length == 0) continue;
                // Luna10 also resized some strips (Header HEADERITEM 18x18 -> 18x20 frames): compare stock frames
                // scaled to the template's frame size; Recut scales the stock strip the same way.
                if (bf[0].Width != tf[0].Width || bf[0].Height != tf[0].Height)
                    bf = bf.Select(f => Scale(f, tf[0].Width, tf[0].Height)).ToArray();

                var sets = new HashSet<int>[tf.Length];
                int matched = 0;
                for (int j = 0; j < tf.Length; ++j)
                {
                    sets[j] = new HashSet<int>(Enumerable.Range(0, bf.Length).Where(i => MeanDiff(bf[i], tf[j]) <= MaxFrameDiff));
                    if (sets[j].Count > 0) ++matched;
                }
                // states win10 added that xp never drew (Button CHECKBOX's four "excluded" frames, 17-20): when at least
                // three quarters of the frames are stock xp's, a frame without a match takes its closest stock frame.
                if (matched == 0 || matched * 4 < tf.Length * 3) continue; // this image isn't a re-cut of stock art
                for (int j = 0; j < tf.Length; ++j)
                {
                    if (sets[j].Count > 0) continue;
                    int best = Enumerable.Range(0, bf.Length).OrderBy(i => MeanDiff(bf[i], tf[j])).First();
                    sets[j].Add(best);
                    guessed.Add(j);
                }
                if (candidates == null) candidates = sets;
                else
                {
                    var narrowed = candidates.Select((c, j) => new HashSet<int>(c.Intersect(sets[j]))).ToArray();
                    if (narrowed.All(c => c.Count > 0)) candidates = narrowed;
                }
            }
            return candidates?.Select((c, j) => c.Contains(j) ? j : c.Min()).ToArray();
        }

        /// <param name="fromTemplate">
        /// stock re-cut only (non-null): where the template has the strip in the new cut, the re-cut stock becomes the
        /// template's strip, so the importer sees a stock strip the template left alone and lets the source's art
        /// replace it. Rebuilt from stock frames it differed in the frames win10 added (Button CHECKBOX's four
        /// "excluded" states) and in Luna10's rescaled dpi slots (25px -> 20px), and Luna10's white boxes stayed under GSM.
        /// </param>
        private void Recut(VisualStyle style, StyleClass cls, int part, StyleState state, List<StyleProperty> images, Geometry from, Geometry to, int[] map, ICollection<int> fromTemplate = null)
        {
            // the template's frame size for this state, from its first image (null: keep the source's size).
            Size? targetFrame = null;
            var tc = m_target.Classes.Values.FirstOrDefault(c => string.Equals(c.ClassName, cls.ClassName, StringComparison.OrdinalIgnoreCase));
            if (tc != null)
                foreach (var prop in images)
                {
                    var tprop = SemanticModel.FindEffective(m_target, tc, part, state.StateId, (IDENTIFIER)prop.Header.nameID);
                    var timg = tprop == null ? null : Decode(m_target, tprop);
                    var tf = timg == null ? null : Frames(timg, to);
                    if (tf != null && tf.Length > 0) { targetFrame = new Size(tf[0].Width, tf[0].Height); break; }
                }
            foreach (var prop in images)
            {
                var img = Decode(style, prop);
                if (img == null) continue;
                var frames = Frames(img, from);
                if (frames.Length == 0) continue;
                // only the stock strip is resized to the template's frames (so it can become the template's strip).
                // the source keeps its own frame size: uxtheme stretches it by SIZINGMARGINS as xp did, while
                // resizing duplicated the middle row/column, the one an arrow glyph sits on (Flarn's fat scroll arrows).
                if (style == m_base && targetFrame is Size ts && prop.Header.nameID == (int)IDENTIFIER.IMAGEFILE && (frames[0].Width != ts.Width || frames[0].Height != ts.Height))
                    frames = frames.Select(f => Scale(f, ts.Width, ts.Height)).ToArray();
                int w = frames[0].Width, h = frames[0].Height;
                ArgbImage adopt = null;
                if (fromTemplate != null && tc != null)
                {
                    var tprop = SemanticModel.FindEffective(m_target, tc, part, state.StateId, (IDENTIFIER)prop.Header.nameID);
                    var timg = tprop == null ? null : Decode(m_target, tprop);
                    if (timg != null && Frames(timg, to).Length == map.Length) adopt = timg;
                }
                if (adopt != null)
                {
                    prop.SetValue(style.AddXpImage(ImageIdentity.EncodePngVerified(adopt)));
                    continue;
                }
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

        /// <summary>
        /// resizes a frame keeping its edges: rows/columns come from the top/left half and the bottom/right half of the
        /// source, the centre one fills (or is dropped from) the middle. Frames are nine-grid art, so a border stays a
        /// border and only the flat middle changes size (18x18 xp header -> 18x20 Luna10 header).
        /// </summary>
        private static ArgbImage Scale(ArgbImage src, int w, int h)
        {
            int Map(int d, int dstLen, int srcLen)
            {
                int head = srcLen / 2, tail = srcLen - head;
                if (d < Math.Min(head, dstLen / 2)) return d;                     // leading edge
                int fromEnd = dstLen - 1 - d;
                if (fromEnd < Math.Min(tail, dstLen - dstLen / 2)) return srcLen - 1 - fromEnd; // trailing edge
                return head;                                                        // stretched centre
            }
            var dst = new ArgbImage(w, h);
            for (int y = 0; y < h; ++y)
            {
                int sy = Map(y, h, src.Height);
                for (int x = 0; x < w; ++x)
                    dst.Pixels[y * w + x] = src.Pixels[sy * src.Width + Map(x, w, src.Width)];
            }
            return dst;
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
