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
                // a missing IMAGELAYOUT means horizontal to xp's loader and vertical to win10's; a source or base
                // strip without the record must be cut the way xp cut it, not the way the template's loader would.
                Horizontal = layout?.GetValue() is int l ? l == 1 : s.Platform == Platform.WinXP,
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
                        var m = images.Count == 0 ? null : LearnMap(WithGlyph(bs, images), tc, donor, bs.StateId,
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
                        // one frame either way: the picture is the whole image and the orientation flag cuts
                        // nothing, so the template didn't re-cut anything here (Luna10 drops IMAGELAYOUT on the
                        // single-picture ExplorerBar headers). Treating it as a re-cut refused those parts and
                        // kept the template's own art over the source's (Pure's NORMALGROUPHEAD/SPECIALGROUPHEAD).
                        if ((bg.Count == 1 && tg.Count == 1) || (bg.Count == tg.Count && bg.Horizontal == tg.Horizontal))
                        {
                            if (sc != null && sc.Parts.TryGetValue(bp.PartId, out var samePart)
                                && samePart.States.TryGetValue(bs.StateId, out var sameState))
                            {
                                var sg = GeometryOf(m_source, sc, bp.PartId, bs.StateId);
                                if (sg.Count != tg.Count)
                                {
                                    string sameLabel = $"{bc.ClassName} part {bp.PartId} state {bs.StateId}";
                                    int[] sourceMap = sg.Count == 1 ? new int[tg.Count]
                                        : SemanticMap(bc.ClassName, bp.PartId, sg.Count, tg.Count);
                                    var ownImages = sameState.Properties.Where(p => StripIds.Contains(p.Header.nameID)).ToList();
                                    if (sourceMap == null || !Recut(m_source, sc, bp.PartId, sameState,
                                        WithGlyph(sameState, ownImages), sg, tg, sourceMap, p => false, sameLabel))
                                        KeepTemplate(bc.ClassName, bp.PartId, sameLabel, $"source {sg} has no semantic mapping to {tg}");
                                }
                            }
                            continue;
                        }
                        string label = $"{bc.ClassName} part {bp.PartId} state {bs.StateId}";

                        int[] map = SemanticMap(bc.ClassName, bp.PartId, bg.Count, tg.Count)
                            ?? LearnMap(WithGlyph(bs, images), tc, bp.PartId, bs.StateId, bg, tg);
                        var donorKey = mapDonors.Keys.FirstOrDefault(k => k.Item2 == bp.PartId && string.Equals(k.Item1, bc.ClassName, StringComparison.OrdinalIgnoreCase));
                        if (map == null && donorKey.Item1 != null
                            && learned.TryGetValue((bc.ClassName.ToLowerInvariant(), mapDonors[donorKey], bs.StateId), out var donorMap) && donorMap.Length == tg.Count)
                        {
                            map = donorMap;
                            m_report.Notes.Add($"{label}: template redrew this strip; it takes part {mapDonors[donorKey]}'s frame map, the part it is drawn on.");
                        }
                        if (map == null && sc != null && IsGripper(bc.ClassName, bp.PartId)
                            && sc.Parts.TryGetValue(bp.PartId, out var gripPart) && gripPart.States.TryGetValue(bs.StateId, out var gripState)
                            && !SamePictures(gripState, images)
                            && RefitOwnFrames(m_source, sc, tc, bp.PartId, bs.StateId, tg, out var refit))
                        {
                            m_report.Notes.Add($"{label}: template redrew the resize grip; the source's own {refit} alignment frames are retained at their native size and expanded into {tg} frames (top alignments reflected vertically).");
                            m_report.Count("xp-strip-refit");
                            continue;
                        }
                        if (map == null)
                        {
                            KeepTemplate(bc.ClassName, bp.PartId, label, $"no semantic or consistent stock-art mapping from {bg} to {tg}");
                            continue;
                        }

                        // the source's pictures, and which of them are still stock xp's. Compared before the stock
                        // is re-cut: a stock picture may adopt the template's strip, a source-only one may not.
                        StyleState ss = null;
                        var simages = new List<StyleProperty>();
                        var stockProps = new HashSet<StyleProperty>();
                        if (sc != null && sc.Parts.TryGetValue(bp.PartId, out var sp) && sp.States.TryGetValue(bs.StateId, out ss))
                        {
                            simages = ss.Properties.Where(p => StripIds.Contains(p.Header.nameID)).ToList();
                            foreach (var prop in WithGlyph(ss, simages))
                            {
                                var bprop = bs.Properties.FirstOrDefault(x => x.Header.nameID == prop.Header.nameID);
                                if (bprop == null) continue;
                                var a = Decode(m_base, bprop);
                                var b = Decode(m_source, prop);
                                if (a != null && b != null && a.PixelSha1() == b.PixelSha1()) stockProps.Add(prop);
                            }
                        }

                        // an image glyph is cut with the part's IMAGECOUNT too: it goes along with the same map
                        // (Toolbar SPLITBUTTONDROPDOWN, the back/forward arrows), or its frames stop lining up.
                        if (!Recut(m_base, bc, bp.PartId, bs, WithGlyph(bs, images), bg, tg, map, p => true, label))
                        {
                            KeepTemplate(bc.ClassName, bp.PartId, label, $"stock frames cannot fill the template's {tg} mapping");
                            continue;
                        }
                        bool remapped = true;
                        if (ss != null && simages.Count > 0)
                        {
                            var sg = GeometryOf(m_source, sc, bp.PartId, bs.StateId);
                            if (sg.Count == bg.Count)
                            {
                                // same frames as stock, maybe laid out the other way (GSM's thumbs are 4 side by side):
                                // frame i means the same state, so the stock map applies to the source's own cut.
                                remapped = Recut(m_source, sc, bp.PartId, ss, WithGlyph(ss, simages), sg, tg, map, stockProps.Contains, label);
                            }
                            else if (sg.Count == tg.Count && SemanticMap(bc.ClassName, bp.PartId, bg.Count, tg.Count) != null)
                            {
                                // A known modern-state strip (notably PUSHBUTTON's sixth animating state)
                                // already has the target's semantic order; never interpret it as stock's cut.
                                remapped = Recut(m_source, sc, bp.PartId, ss, WithGlyph(ss, simages), sg, tg,
                                    Enumerable.Range(0, tg.Count).ToArray(), p => false, label);
                            }
                            else if (sg.Count == 1)
                            {
                                // one frame draws every state on xp; each template frame shows it.
                                remapped = Recut(m_source, sc, bp.PartId, ss, WithGlyph(ss, simages), sg, tg, new int[tg.Count], p => false, label);
                                if (remapped)
                                    m_report.Notes.Add($"{label}: source draws every state with one frame; repeated into the template's {tg} frames.");
                            }
                            else
                                remapped = false;
                        }
                        if (!remapped)
                        {
                            KeepTemplate(bc.ClassName, bp.PartId, label, $"source frame count/layout has no semantic mapping to {tg}");
                            continue;
                        }
                        m_report.Notes.Add($"{label}: strip re-cut from {bg} to {tg} frames the way the template did it (frames {string.Join(",", map.Select(i => i + 1))}).");
                        m_report.Count("xp-strip-remapped");
                    }
                }
            }
        }

        private void KeepTemplate(string cls, int part, string label, string reason)
        {
            var sourceNames = SemanticModel.PartNames(cls, m_base.Platform);
            int targetPart = part;
            if (sourceNames.TryGetValue(part, out var name))
            {
                var targetName = SemanticModel.PartNames(cls, m_target.Platform)
                    .FirstOrDefault(p => string.Equals(p.Value, name, StringComparison.OrdinalIgnoreCase));
                if (targetName.Value != null) targetPart = targetName.Key;
            }
            m_report.UnmappedStripParts.Add(cls + "|" + targetPart);
            m_report.Warnings.Add($"{label}: {reason}; this part's strip geometry and images are locked to the template.");
            m_report.Count("xp-strip-not-remapped");
        }

        // These are state contracts, not visual guesses. HOVER means the pointer is elsewhere on
        // the scrollbar, unlike HOT (the pointer is on this particular thumb/arrow).
        private static int[] SemanticMap(string cls, int part, int sourceCount, int targetCount)
        {
            int separator = cls.LastIndexOf("::", StringComparison.Ordinal);
            if (separator >= 0) cls = cls.Substring(separator + 2);
            if (string.Equals(cls, "Button", StringComparison.OrdinalIgnoreCase) && part == 1
                && sourceCount == 5 && targetCount == 6)
                return new[] { 0, 1, 2, 3, 4, 4 }; // DEFAULTED_ANIMATING uses DEFAULTED on XP
            // CHECKBOX: xp has 12 states (unchecked/checked/mixed x normal/hot/pressed/disabled); win10 keeps
            // those 1-12 and adds IMPLICITNORMAL/HOT/PRESSED/DISABLED (13-16) and EXCLUDEDNORMAL/HOT/PRESSED/
            // DISABLED (17-20). IMPLICIT means "checked by the parent", so it draws the checked art; EXCLUDED
            // means "not applicable", which draws the unchecked art. Stock Luna10's own strip confirms it:
            // frames 13-16 are pixel-identical to frames 5-8, and 17-20 are the unchecked box with an X.
            if (string.Equals(cls, "Button", StringComparison.OrdinalIgnoreCase) && part == 3
                && sourceCount == 12 && targetCount == 20)
                return new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 4, 5, 6, 7, 0, 1, 2, 3 };
            if (string.Equals(cls, "ScrollBar", StringComparison.OrdinalIgnoreCase))
            {
                if (part == 1 && sourceCount == 16 && targetCount == 20)
                    return Enumerable.Range(0, 16).Concat(new[] { 0, 4, 8, 12 }).ToArray();
                if (part >= 2 && part <= 9 && sourceCount == 4 && targetCount == 5)
                    return new[] { 0, 1, 2, 3, 0 };
            }
            // SPIN: xp and win10 both have four states (NORMAL, HOT, PRESSED, DISABLED) and both index the strip
            // by state id (uxtheme imagefile.cpp GetOffsets: offset = (iStateId-1) * single). A source that ships
            // five frames therefore has one xp never draws - a spare the pack author added, not a state. Taking the
            // first four keeps the source's own art; refusing the cut (5 has no mapping to 4) left the template's
            // Luna-blue spinner on themes that ship their own (Black Aero Vista's white/blue Vista button).
            if (string.Equals(cls, "Spin", StringComparison.OrdinalIgnoreCase) && part >= 1 && part <= 4
                && sourceCount == 5 && targetCount == 4)
                return new[] { 0, 1, 2, 3 };
            return null;
        }

        /// <summary>Both native resize-grip consumers use right/left alignment frames on XP.</summary>
        private static bool IsGripper(string className, int part) =>
            (string.Equals(className, "Status", StringComparison.OrdinalIgnoreCase) && part == 3)
            || (string.Equals(className, "ScrollBar", StringComparison.OrdinalIgnoreCase) && part == 10);

        /// <summary>
        /// XP supplies right/left size grips; Win10 adds top and half alignment states. Keep each
        /// consumer's own art and native true-size dimensions, not the template's redrawn glyph size.
        /// The eight states are right, left, top-right, top-left, then their half-size counterparts.
        /// The latter use the same full motif in the template; only the top variants need reflection.
        /// Stock source art is deliberately not refitted, preserving the template's stock adaptations.
        /// </summary>
        private bool RefitOwnFrames(VisualStyle srcStyle, StyleClass sc, StyleClass tc, int part, int state, Geometry tg, out string sourceCut)
        {
            sourceCut = null;
            if (sc == null || !sc.Parts.TryGetValue(part, out var sp) || !sp.States.TryGetValue(state, out var ss)) return false;
            var sg = GeometryOf(srcStyle, sc, part, state);
            if ((sg.Count != 1 && sg.Count != 2) || tg.Count != 8) return false;
            var images = ss.Properties.Where(p => StripIds.Contains(p.Header.nameID)).ToList();
            var decoded = images.Select(p => Decode(srcStyle, p)).ToList();
            if (images.Count == 0 || decoded.Any(p => p == null || (sg.Horizontal ? p.Width : p.Height) < sg.Count)) return false;
            sourceCut = sg.ToString();
            for (int i = 0; i < images.Count; ++i)
            {
                var frames = Frames(decoded[i], sg);
                int w = frames[0].Width, h = frames[0].Height;
                var outImg = new ArgbImage(tg.Horizontal ? w * tg.Count : w, tg.Horizontal ? h : h * tg.Count);
                for (int j = 0; j < tg.Count; ++j)
                {
                    bool left = MirroredLeft(tc, part, j), top = MirroredTop(tc, part, j);
                    var frame = frames[left && frames.Length > 1 ? 1 : 0];
                    int ox = tg.Horizontal ? j * w : 0, oy = tg.Horizontal ? 0 : j * h;
                    for (int y = 0; y < h; ++y)
                        for (int x = 0; x < w; ++x)
                            outImg.Pixels[(oy + y) * outImg.Width + ox + x] = frame.Pixels[(top ? h - y - 1 : y) * w + (left && frames.Length == 1 ? w - x - 1 : x)];
                }
                images[i].SetValue(srcStyle.AddXpImage(ImageIdentity.EncodePngVerified(outImg)));
            }
            Upsert(ss, sc, part, IDENTIFIER.IMAGECOUNT, IDENTIFIER.INT, tg.Count);
            Upsert(ss, sc, part, IDENTIFIER.IMAGELAYOUT, IDENTIFIER.ENUM, tg.Horizontal ? 1 : 0);
            return true;
        }

        /// <summary>
        /// whether the template's frame j is the left-aligned variant, read from the alignment states win10 gives
        /// the part (SIZEBOX RIGHTALIGN/LEFTALIGN/...); the gripper layouts follow right, left, top-right, top-left,
        /// then the half variants, so parity is the fallback when the port names its states generically (Status
        /// GRIPPER has a single Common state).
        /// </summary>
        private static bool MirroredLeft(StyleClass tc, int part, int j)
        {
            var name = StateName(tc, part, j);
            if (name != null)
            {
                if (name.IndexOf("LEFT", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (name.IndexOf("RIGHT", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
            return (j & 1) != 0;
        }

        private static bool MirroredTop(StyleClass tc, int part, int j)
        {
            var name = StateName(tc, part, j);
            if (name != null)
            {
                if (name.IndexOf("TOP", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (name.IndexOf("BOTTOM", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
            return (j & 2) != 0;
        }

        /// <summary>name of the win10 state a strip frame belongs to (frame 0 = state 1, since state 0 is Common).</summary>
        private static string StateName(StyleClass tc, int part, int frame)
        {
            if (tc == null || !tc.Parts.TryGetValue(part, out var tp)) return null;
            if (!tp.States.TryGetValue(frame + 1, out var st)) return null;
            return string.IsNullOrEmpty(st.StateName) ? null : st.StateName;
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
        /// Recover only frame correspondences supported by every comparable stock image.
        /// Redrawn frames and contradictory background/glyph mappings are not nearest-frame
        /// candidates: only an explicit state contract can supply those mappings.
        /// </summary>
        private int[] LearnMap(List<StyleProperty> baseImages, StyleClass tc, int part, int state, Geometry bg, Geometry tg)
        {
            HashSet<int>[] intersection = null;
            foreach (var bprop in baseImages)
            {
                var tprop = SemanticModel.FindEffective(m_target, tc, part, state, (IDENTIFIER)bprop.Header.nameID);
                if (tprop == null) continue;
                var bimg = Decode(m_base, bprop);
                var timg = Decode(m_target, tprop);
                if (bimg == null || timg == null) return null;
                var bf = Frames(bimg, bg);
                var tf = Frames(timg, tg);
                if (bf.Length == 0 || tf.Length == 0) return null;
                if (bf[0].Width != tf[0].Width || bf[0].Height != tf[0].Height)
                    bf = bf.Select(f => Scale(f, tf[0].Width, tf[0].Height)).ToArray();
                if (intersection == null) intersection = new HashSet<int>[tf.Length];
                if (intersection.Length != tf.Length) return null;
                for (int j = 0; j < tf.Length; ++j)
                {
                    var candidates = new HashSet<int>(Enumerable.Range(0, bf.Length)
                        .Where(i => MeanDiff(bf[i], tf[j]) <= MaxFrameDiff));
                    if (intersection[j] == null) intersection[j] = candidates;
                    else intersection[j].IntersectWith(candidates);
                    if (intersection[j].Count == 0) return null;
                }
            }
            if (intersection == null) return null;
            var map = new int[intersection.Length];
            for (int j = 0; j < map.Length; ++j)
                map[j] = intersection[j].Contains(j) ? j
                    : intersection[j].Count == 1 ? intersection[j].Single() : -1;
            if (map.Any(i => i < 0)) return null;
            return map;
        }

        /// <param name="adoptFromTemplate">
        /// true for a picture that is still stock xp's: where the template has the strip in the new cut, the re-cut
        /// stock becomes the template's strip, so the importer sees a stock strip the template left alone and lets
        /// the source's art replace it. Rebuilt from stock frames it differed in the frames win10 added (Button
        /// CHECKBOX's four "excluded" states) and in Luna10's rescaled dpi slots (25px -> 20px), and Luna10's white
        /// boxes stayed under GSM. A source picture that is its own art never adopts the template's frames.
        /// </param>
        /// <returns>false (with no change made) when one of the pictures doesn't have enough frames for the map.</returns>
        private bool Recut(VisualStyle style, StyleClass cls, int part, StyleState state, List<StyleProperty> images, Geometry from, Geometry to, int[] map, Func<StyleProperty, bool> adoptFromTemplate, string label)
        {
            if (map.Length == 0) return false;
            int max = map.Max();

            // decode/validate first: a strip with fewer frames than the map references would throw halfway through,
            // leaving a half re-cut state behind. Skip the condition instead.
            var work = new List<(StyleProperty Prop, ArgbImage[] Frames)>();
            foreach (var prop in images)
            {
                var img = Decode(style, prop);
                if (img == null) return false;
                var frames = Frames(img, from);
                if (frames.Length == 0) return false;
                if (max >= frames.Length)
                {
                    m_report.Warnings.Add($"{label}: {prop.Header.nameID} has {frames.Length} frames, which can't fill the template's {map.Length}-frame mapping (frame {max + 1} is missing); the strip was left as is.");
                    return false;
                }
                work.Add((prop, frames));
            }
            if (work.Count == 0) return false;

            // the template's frame size for this state, from its first image (null: keep the source's size).
            Size? targetFrame = null;
            var tc = m_target.Classes.Values.FirstOrDefault(c => string.Equals(c.ClassName, cls.ClassName, StringComparison.OrdinalIgnoreCase));
            if (tc != null)
                foreach (var prop in work)
                {
                    var tprop = SemanticModel.FindEffective(m_target, tc, part, state.StateId, (IDENTIFIER)prop.Prop.Header.nameID);
                    var timg = tprop == null ? null : Decode(m_target, tprop);
                    var tf = timg == null ? null : Frames(timg, to);
                    if (tf != null && tf.Length > 0) { targetFrame = new Size(tf[0].Width, tf[0].Height); break; }
                }
            foreach (var (prop, decoded) in work)
            {
                var frames = decoded;
                // only the stock strip is resized to the template's frames (so it can become the template's strip).
                // the source keeps its own frame size: uxtheme stretches it by SIZINGMARGINS as xp did, while
                // resizing duplicated the middle row/column, the one an arrow glyph sits on (Flarn's fat scroll arrows).
                if (style == m_base && targetFrame is Size ts && prop.Header.nameID == (int)IDENTIFIER.IMAGEFILE && (frames[0].Width != ts.Width || frames[0].Height != ts.Height))
                    frames = frames.Select(f => Scale(f, ts.Width, ts.Height)).ToArray();
                int w = frames[0].Width, h = frames[0].Height;
                ArgbImage adopt = null;
                if (adoptFromTemplate != null && adoptFromTemplate(prop) && tc != null)
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
            return true;
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
