using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace libmsstyle.Import
{
    /// <summary>
    /// Offline checks of an import candidate against the template it was built from. Nothing here
    /// loads the style into uxtheme or applies it; passing means "structurally consistent", not
    /// "renders correctly" and not "won't black-screen the desktop".
    /// </summary>
    public class StyleValidator
    {
        private readonly string m_template;
        private readonly string m_candidate;
        private readonly IReadOnlyDictionary<string, byte[]> m_added;
        private readonly List<SpriteExpectation> m_sprites;
        private readonly HashSet<string> m_changed;
        private readonly bool m_stringsAdded;
        private readonly HashSet<string> m_geometryWaivers;
        private readonly List<string> m_addedRecords;
        private readonly List<string> m_removedRecords;
        private readonly HashSet<string> m_frameWaivers;
        private readonly IReadOnlyDictionary<int, string> m_addedClasses;
        private readonly IReadOnlyDictionary<string, byte[]> m_rewrittenMaps;
        private readonly ValidationReport m_rep = new ValidationReport();

        /// <param name="added">"TYPE/id" -> bytes for every resource the import added.</param>
        /// <param name="stringsAdded">the import added font strings, so string tables (and a .mui link) were rewritten.</param>
        /// <param name="geometryWaivers">DWMWindow parts kept on purpose with geometry the check doesn't like (shipped that way by the source).</param>
        /// <param name="frameWaivers"><see cref="FrameWaiverKey"/>s of source images that ship with a remainder after splitting into IMAGECOUNT frames.</param>
        internal StyleValidator(string template, string candidate, IReadOnlyDictionary<string, byte[]> added,
            List<SpriteExpectation> sprites, List<string> changedLabels, bool stringsAdded = false, IEnumerable<string> geometryWaivers = null,
            IEnumerable<string> addedRecords = null, IEnumerable<string> frameWaivers = null, IEnumerable<string> removedRecords = null,
            IReadOnlyDictionary<int, string> addedClasses = null, IReadOnlyDictionary<string, byte[]> rewrittenMaps = null)
        {
            m_template = template;
            m_candidate = candidate;
            m_added = added ?? new Dictionary<string, byte[]>();
            m_sprites = sprites ?? new List<SpriteExpectation>();
            m_changed = new HashSet<string>(changedLabels ?? new List<string>(), StringComparer.Ordinal);
            m_stringsAdded = stringsAdded;
            m_geometryWaivers = new HashSet<string>(geometryWaivers ?? new string[0], StringComparer.OrdinalIgnoreCase);
            m_addedRecords = (addedRecords ?? new string[0]).ToList();
            m_removedRecords = (removedRecords ?? new string[0]).ToList();
            m_frameWaivers = new HashSet<string>(frameWaivers ?? new string[0], StringComparer.Ordinal);
            m_addedClasses = addedClasses ?? new Dictionary<int, string>();
            m_rewrittenMaps = rewrittenMaps ?? new Dictionary<string, byte[]>();
        }

        /// <summary>No-op check: the candidate must equal the template except for the listed additions.</summary>
        public static ValidationReport ValidateUnchanged(string template, string candidate)
        {
            return new StyleValidator(template, candidate, null, null, null).Validate();
        }

        private void Error(string s) { m_rep.Errors.Add(s); }
        private void Warn(string s) { m_rep.Warnings.Add(s); }
        private void Check(string s) { m_rep.Checks.Add(s); }

        public ValidationReport Validate()
        {
            try
            {
                using (var tInv = new ResourceInventory(m_template))
                using (var cInv = new ResourceInventory(m_candidate))
                using (var t = new VisualStyle())
                using (var c = new VisualStyle())
                {
                    t.Load(m_template);
                    c.Load(m_candidate);
                    CheckResources(tInv, cInv);
                    CheckParse(t, c, cInv);
                    CheckClasses(t, c);
                    CheckRecords(t, c, tInv, cInv);
                    var tm = new SemanticModel(t);
                    var cm = new SemanticModel(c);
                    CheckReferences(tm, cm);
                    CheckPackthem(cInv);
                    CheckDwm(tm, cm);
                    CheckStrings(tm, cm);
                    CheckFonts(tm, cm);
                    CheckMetrics(tm, cm);
                }
            }
            catch (Exception ex)
            {
                Error("validation crashed: " + ex.Message);
            }
            return m_rep;
        }

        private void CheckResources(ResourceInventory tInv, ResourceInventory cInv)
        {
            var cByKey = cInv.Entries.ToDictionary(e => e.Key);
            var tByKey = tInv.Entries.ToDictionary(e => e.Key);
            int same = 0;
            foreach (var te in tInv.Entries)
            {
                bool stringBucket = te.Type == "#6";
                bool muiLink = te.Type == "MUI";
                if (!cByKey.TryGetValue(te.Key, out var ce))
                {
                    // the standalone save moves the .mui's strings into the .msstyles and drops the link; it also
                    // rewrites the buckets from the loaded tables, so bucket numbering can shift. the text is
                    // compared per language in CheckStringTables.
                    if (muiLink && m_stringsAdded) continue;
                    if (stringBucket && m_stringsAdded) continue;
                    Error($"resource {te.Key} from the template is missing in the candidate");
                    continue;
                }
                if (muiLink && m_stringsAdded) continue;
                if (stringBucket)
                {
                    // the text is compared per language in CheckStringTables; the bucket bytes themselves
                    // only survive untouched when no string was added.
                    if (!m_stringsAdded)
                    {
                        if (te.Sha1 != ce.Sha1) Error($"string bucket {te.Key} changed but no string was added");
                        else same++;
                    }
                    continue;
                }
                if (m_rewrittenMaps.TryGetValue(te.Key, out var rewritten))
                {
                    if (!cInv.GetData(ce).SequenceEqual(rewritten)) Error($"resource {te.Key} differs from the declared class-map rewrite");
                    continue;
                }
                if (te.Type == "VARIANT")
                {
                    if (m_changed.Count == 0)
                    {
                        if (te.Sha1 != ce.Sha1) Error("no property was changed, yet VARIANT/NORMAL bytes differ from the template");
                        else Check("nothing changed and VARIANT/NORMAL is byte-identical to the template.");
                    }
                    continue;
                }
                string addKey = te.Type + "/" + (te.TryGetIntName(out int id) ? id.ToString() : te.Name);
                if (te.Sha1 != ce.Sha1)
                    Error($"resource {te.Key} changed but the import never rewrites existing resources");
                else same++;
                if (m_added.ContainsKey(addKey))
                    Error($"resource {addKey} was allocated by the import but already existed in the template");
            }
            Check($"{same} template resources are byte-identical; VARIANT, declared class-map rewrites and added-font string buckets are checked separately.");

            int added = 0;
            foreach (var ce in cInv.Entries.Where(e => !tByKey.ContainsKey(e.Key) && !(m_stringsAdded && e.Type == "#6")))
            {
                string addKey = ce.Type + "/" + (ce.TryGetIntName(out int id) ? id.ToString() : ce.Name);
                if (!m_added.TryGetValue(addKey, out var bytes))
                {
                    Error($"candidate has an unexpected new resource {ce.Key}");
                    continue;
                }
                if (ResourceInventory.Sha1Hex(bytes) != ce.Sha1) Error($"new resource {ce.Key} doesn't hold the bytes the import queued");
                else added++;
            }
            if (added != m_added.Count) Error($"import queued {m_added.Count} new resources, candidate has {added} of them");
            Check($"{added} new IMAGE/STREAM resources, ids unused in the template, bytes verified.");
        }

        private void CheckParse(VisualStyle t, VisualStyle c, ResourceInventory cInv)
        {
            foreach (var d in c.LoadDiagnostics) Error("candidate property stream: " + d);
            if (c.Platform != t.Platform) Error($"candidate is detected as {c.Platform}, template as {t.Platform}");
            var stored = cInv.GetData(cInv.Entries.First(e => e.Type == "VARIANT"));
            var reser = c.SerializeProperties();
            if (!stored.SequenceEqual(reser))
            {
                int at = 0;
                while (at < stored.Length && at < reser.Length && stored[at] == reser[at]) at++;
                Error($"candidate VARIANT doesn't survive parse -> serialize unchanged (stored {stored.Length} bytes, re-serialized {reser.Length}, first difference at 0x{at:X})");
            }
            else Check($"candidate VARIANT ({stored.Length} bytes) parses without diagnostics and re-serializes byte-identically.");
        }

        private void CheckClasses(VisualStyle t, VisualStyle c)
        {
            if (c.Classes.Count != t.Classes.Count + m_addedClasses.Count)
                Error($"class count changed {t.Classes.Count} -> {c.Classes.Count}, expected {m_addedClasses.Count} declared additions");
            foreach (var kv in t.Classes)
            {
                if (!c.Classes.TryGetValue(kv.Key, out var cc) || cc.ClassName != kv.Value.ClassName)
                    Error($"class {kv.Key} ({kv.Value.ClassName}) changed name or ID");
                if (m_addedClasses.Count == 0 && cc != null && cc.BaseClassId != kv.Value.BaseClassId)
                    Error($"class {kv.Key} ({kv.Value.ClassName}) changed base class");
            }
            foreach (var kv in m_addedClasses)
                if (t.Classes.ContainsKey(kv.Key) || !c.Classes.TryGetValue(kv.Key, out var cls) || cls.ClassName != kv.Value)
                    Error($"declared appended class {kv.Key} ({kv.Value}) is missing or overwrote an existing class");
            Check($"existing CMAP class IDs and names preserved; {m_addedClasses.Count} declared classes appended.");
        }

        /// <summary>
        /// "3 removed records (IMAGEFILE x2, BGTYPE x1)" for the declared removals. They aren't all colors:
        /// the xp passes drop IMAGEFILE/BGTYPE/IMAGELAYOUT/SIZINGMARGINS/SIZINGTYPE records too.
        /// </summary>
        private string RemovedSummary()
        {
            var byProperty = new List<string>();
            foreach (var g in m_removedRecords
                .Select(r => r.Split('/'))
                .GroupBy(p => p.Length > 3 && int.TryParse(p[3], out int id) ? SemanticModel.PropertyName(id) : "?")
                .OrderBy(g => g.Key, StringComparer.Ordinal))
                byProperty.Add(g.Count() == 1 ? g.Key : $"{g.Key} x{g.Count()}");
            return $"{m_removedRecords.Count} removed record{(m_removedRecords.Count == 1 ? "" : "s")} ({string.Join(", ", byProperty)})";
        }

        private static IEnumerable<string> RecordKeys(VisualStyle s)
        {
            foreach (var cls in s.Classes.Values)
                foreach (var part in cls.Parts.Values)
                    foreach (var st in part.States.Values)
                        foreach (var p in st.Properties)
                            yield return $"{cls.ClassId}/{part.PartId}/{st.StateId}/{p.Header.nameID}/{p.Header.typeID}";
        }

        private static bool HasRecords(StyleClass cls) => cls.Parts.Values.Any(p => p.States.Values.Any(st => st.Properties.Count > 0));

        private void CheckRecords(VisualStyle t, VisualStyle c, ResourceInventory tInv, ResourceInventory cInv)
        {
            var expected = RecordKeys(t).Concat(m_addedRecords).ToList();
            foreach (var removed in m_removedRecords)
            {
                // one occurrence per removal, a state can hold the same name/type twice.
                if (!expected.Remove(removed)) Error($"declared removal {removed} isn't a template record");
            }
            var tk = expected.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var ck = RecordKeys(c).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!tk.SequenceEqual(ck)) Error("candidate's property records aren't the template's plus the declared additions and minus the declared removals");
            else Check($"{ck.Count} property records: the template's set{(m_addedRecords.Count > 0 ? $" plus {m_addedRecords.Count} added records" : "")}{(m_removedRecords.Count > 0 ? $" minus {RemovedSummary()}" : "")}, nothing else.");

            // uxtheme registers only classes that have VARIANT records and BCMAP parents index that
            // registration order (18362 LoadClassDataMap / AddIndexInternal). a class gaining its first
            // record or losing its last one shifts the parent of every class after it.
            var tReg = new HashSet<int>(t.Classes.Values.Where(HasRecords).Select(x => x.ClassId));
            var cReg = new HashSet<int>(c.Classes.Values.Where(HasRecords).Select(x => x.ClassId));
            var lost = tReg.Except(cReg).Select(id => t.Classes[id].ClassName).ToList();
            var gained = cReg.Except(tReg).Where(id => !m_addedClasses.ContainsKey(id)).Select(id => c.Classes[id].ClassName).ToList();
            if (lost.Count > 0) Error($"classes lost all their records, which shifts every later BCMAP parent: {string.Join(", ", lost)}");
            if (gained.Count > 0) Error($"undeclared classes got their first records: {string.Join(", ", gained)}");
            if (lost.Count == 0 && gained.Count == 0) Check($"registered class changes are limited to {m_addedClasses.Count} declared appended classes.");

            // uxtheme 19041 builds one section per class/part/state and fails with a duplicate
            // section error when a tuple shows up again later in the stream.
            var data = cInv.GetData(cInv.Entries.First(e => e.Type == "VARIANT"));
            var closed = new HashSet<string>();
            string current = null;
            int cursor = 0, n = 0;
            var sizeByType = ExternalRecordSizes(tInv.GetData(tInv.Entries.First(e => e.Type == "VARIANT")));
            var badSize = new List<string>();
            int lastData = LastNonZero(data);
            while (cursor < data.Length - 4)
            {
                int start = cursor;
                if (cursor > lastData) break;
                StyleProperty p;
                try { p = PropertyStream.ReadNextProperty(data, ref cursor); }
                catch (Exception ex)
                {
                    Error($"candidate VARIANT unreadable at {start}: {ex.Message}");
                    return;
                }
                if (cursor <= start) { Error($"candidate VARIANT parser stalled at {start}"); return; }
                string tuple = $"{p.Header.classID}/{p.Header.partID}/{p.Header.stateID}";
                if (tuple != current)
                {
                    if (current != null) closed.Add(current);
                    if (closed.Contains(tuple))
                    {
                        Error($"class/part/state {tuple} isn't contiguous in the candidate VARIANT");
                        return;
                    }
                    current = tuple;
                }
                n++;
                // records with an external id carry no payload, but the declared size is still read:
                // a FONT declaring anything but a LOGFONTW (0x5C) makes win10 reject the whole theme
                // (seen live with an xp import that left the INI string length there).
                if (p.Header.shortFlag != 0 && sizeByType.TryGetValue(p.Header.typeID, out int want) && p.Header.sizeInBytes != want)
                    badSize.Add($"{tuple} name {p.Header.nameID}: declares {p.Header.sizeInBytes} bytes, the template's type {p.Header.typeID} records declare {want}");
            }
            Check($"{n} records read sequentially; every class/part/state group is contiguous.");
            foreach (var b in badSize.Take(20)) Error("record size: " + b);
            if (badSize.Count == 0) Check("every external-id record declares the same size as the template's records of its type.");

            // same reason as the class set above: the order classes first show up in VARIANT is
            // the registration order BCMAP parents are relative to.
            var tOrder = ClassOrder(tInv.GetData(tInv.Entries.First(e => e.Type == "VARIANT")));
            var cOrder = ClassOrder(data);
            if (!tOrder.SequenceEqual(cOrder.Where(id => !m_addedClasses.ContainsKey(id))))
                Error("existing classes show up in VARIANT in a different order than in the template");
            else Check($"{tOrder.Count} existing classes show up in VARIANT in the template's order.");
            if (m_addedClasses.Count > 0) CheckBaseRegistration(t, c, tInv, cInv);
        }

        private void CheckBaseRegistration(VisualStyle template, VisualStyle candidate, ResourceInventory tInv, ResourceInventory cInv)
        {
            var before = Win7StyleImporter.RegistrationOrder(template);
            var after = Win7StyleImporter.RegistrationOrder(candidate);
            var tb = tInv.GetData(tInv.Entries.First(e => e.Type == "BCMAP"));
            var cb = cInv.GetData(cInv.Entries.First(e => e.Type == "BCMAP"));
            int oldCount = BitConverter.ToInt32(tb, 0), newCount = BitConverter.ToInt32(cb, 0);
            if (oldCount < before.Count || newCount < after.Count || 4L + oldCount * 4L > tb.Length || 4L + newCount * 4L > cb.Length)
            {
                Error("BCMAP does not cover its complete registration order");
                return;
            }
            for (int i = 0; i < before.Count; ++i)
            {
                int oldParent = BitConverter.ToInt32(tb, 4 + i * 4);
                int at = after.IndexOf(before[i]);
                if (at < 0) { Error($"registered class {before[i]} disappeared"); continue; }
                int newParent = BitConverter.ToInt32(cb, 4 + at * 4);
                if (oldParent < 0 ? newParent != oldParent
                    : oldParent < before.Count ? newParent < 0 || newParent >= after.Count || before[oldParent] != after[newParent]
                    : newParent != oldParent + m_addedClasses.Count)
                    Error($"BCMAP parent of existing class {before[i]} changed its registered target");
            }
            foreach (var cls in m_addedClasses)
            {
                int at = after.IndexOf(cls.Key);
                if (at < 0) { Error($"appended class {cls.Value} has no registered records"); continue; }
                int parent = BitConverter.ToInt32(cb, 4 + at * 4);
                if (parent >= after.Count || parent < -1) Error($"appended class {cls.Value} has an invalid BCMAP parent");
            }
            Check("BCMAP preserves every existing registered parent across the class append, including AMAP-only registrations.");
        }


        /// <summary>type id -> declared size of external-id records, where the template is consistent.</summary>
        private static Dictionary<int, int> ExternalRecordSizes(byte[] data)
        {
            var seen = new Dictionary<int, HashSet<int>>();
            int cursor = 0;
            while (cursor < data.Length - 4)
            {
                int start = cursor;
                StyleProperty p;
                try { p = PropertyStream.ReadNextProperty(data, ref cursor); }
                catch (Exception) { break; }
                if (cursor <= start) break;
                if (p.Header.shortFlag == 0) continue;
                if (!seen.TryGetValue(p.Header.typeID, out var s)) seen[p.Header.typeID] = s = new HashSet<int>();
                s.Add(p.Header.sizeInBytes);
            }
            return seen.Where(kv => kv.Value.Count == 1).ToDictionary(kv => kv.Key, kv => kv.Value.First());
        }

        private static List<int> ClassOrder(byte[] data)
        {
            var order = new List<int>();
            var seen = new HashSet<int>();
            int cursor = 0;
            int lastData = LastNonZero(data);
            while (cursor < data.Length - 4)
            {
                if (cursor > lastData) break;
                int start = cursor;
                var p = PropertyStream.ReadNextProperty(data, ref cursor);
                if (cursor <= start) break;
                if (seen.Add(p.Header.classID)) order.Add(p.Header.classID);
            }
            return order;
        }

        /// <summary>index of the last nonzero byte, -1 for none: the zero padding after the last record starts after it.</summary>
        private static int LastNonZero(byte[] data)
        {
            int i = data.Length - 1;
            while (i >= 0 && data[i] == 0) i--;
            return i;
        }

        private void CheckReferences(SemanticModel tm, SemanticModel cm)
        {
            int images = 0, fonts = 0;
            var templateEntries = tm.Entries.ToDictionary(e => (e.ClassId, e.Key));
            foreach (var ce in cm.Entries)
            {
                templateEntries.TryGetValue((ce.ClassId, ce.Key), out var te);
                string label = $"{ce.ClassName}|{ce.PartName}|S{ce.StateId}|{ce.PropertyName}";
                bool changed = m_changed.Contains(label) || (te != null && te.RawValue != ce.RawValue);
                if (te != null && te.RawValue != ce.RawValue && !m_changed.Contains(label))
                    Error($"{label} changed but the import didn't record changing it (template {te.RawValue} -> candidate {ce.RawValue})");

                if (ce.ResourceType != null)
                {
                    var info = cm.GetImageInfo(ce.ResourceType, ce.ResourceId);
                    var tinfo = te == null ? null : tm.GetImageInfo(te.ResourceType, te.ResourceId);
                    if (info == null)
                    {
                        if (tinfo == null && !changed) Warn($"{label}: {ce.ResourceType} {ce.ResourceId} missing, same in the template");
                        else Error($"{label}: {ce.ResourceType} {ce.ResourceId} doesn't exist");
                        continue;
                    }
                    if (changed)
                    {
                        if (SemanticModel.MustBePng(ce.Property))
                        {
                            if (!info.Decodable) Error($"{label}: {ce.ResourceType} {ce.ResourceId} {info.Error}");
                            else if (info.Width <= 0 || info.Height <= 0) Error($"{label}: empty image");
                            else CheckFrames(tm, te, tinfo, cm, ce, info, label);
                        }
                        images++;
                    }
                }
                else if (ce.IsStringRef)
                {
                    if (cm.ResolveString(ce.Property.Header.shortFlag) == null)
                    {
                        if (!changed && te != null && tm.ResolveString(te.Property.Header.shortFlag) == null)
                            Warn($"{label}: string {ce.Property.Header.shortFlag} missing, same in the template");
                        else Error($"{label}: string {ce.Property.Header.shortFlag} doesn't exist");
                    }
                    else if (changed) fonts++;
                }
            }
            Check($"every IMAGE/STREAM reference resolves; {images} changed image references decode as png and split into their effective IMAGECOUNT; {fonts} changed font/string references resolve in the string table.");
        }

        /// <summary>
        /// A strip image should divide into IMAGECOUNT frames along IMAGELAYOUT (0 vertical,
        /// 1 horizontal), with both looked up the way uxtheme falls back (state, state 0, part 0,
        /// base class). Only an error when the template's picture passed the same check and the
        /// source doesn't ship this exact picture with this count: win7 drew those (e.g. a 9x121
        /// trackbar thumb in 5) and no uxtheme check rejecting a remainder was found.
        /// </summary>
        private void CheckFrames(SemanticModel tm, SemanticEntry te, ImageInfo tinfo, SemanticModel cm, SemanticEntry ce, ImageInfo info, string label)
        {
            if (ce.NameId == (int)IDENTIFIER.GLYPHIMAGEFILE) return;
            bool ok = FramesFit(cm, ce, info, out int count, out string why);
            if (ok) return;
            bool templateOk = te != null && tinfo != null && tinfo.Decodable && FramesFit(tm, te, tinfo, out _, out _);
            if (m_frameWaivers.Contains(FrameWaiverKey(info.PixelSha1, count)))
                Warn($"{label}: {why} (same as the source ships it, check it in the VM)");
            else if (templateOk) Error($"{label}: {why}");
            else Warn($"{label}: {why} (template has the same problem)");
        }

        // picture and count, not the property or part: a filled dpi slot or a shared image id carries the
        // source's strip into another property (aero11 shares one picture between Status GRIPPER and ScrollBar SIZEBOX).
        internal static string FrameWaiverKey(string pixelSha1, int count) => pixelSha1 + "|" + count;

        internal static bool FramesFit(SemanticModel m, SemanticEntry e, ImageInfo info, out int count, out string why)
        {
            why = null;
            var cls = m.Style.Classes[e.ClassId];
            var countProp = SemanticModel.FindEffective(m.Style, cls, e.PartId, e.StateId, IDENTIFIER.IMAGECOUNT);
            var layoutProp = SemanticModel.FindEffective(m.Style, cls, e.PartId, e.StateId, IDENTIFIER.IMAGELAYOUT);
            count = countProp?.GetValue() is int c && c > 0 ? c : 1;
            bool horizontal = layoutProp?.GetValue() is int l && l == 1;
            int len = horizontal ? info.Width : info.Height;
            if (len % count == 0) return true;
            why = $"{info.Width}x{info.Height} image doesn't split into {count} {(horizontal ? "horizontal" : "vertical")} frames";
            return false;
        }

        private void CheckPackthem(ResourceInventory cInv)
        {
            // uxtheme 19041 LoadThemeLibrary: PACKTHEM_VERSION/1 must be exactly 2 bytes holding 4.
            var e = cInv.Entries.FirstOrDefault(x => x.Type == "PACKTHEM_VERSION" && x.Name == "#1");
            var d = e == null ? null : cInv.GetData(e);
            if (d == null || d.Length != 2 || BitConverter.ToInt16(d, 0) != 4)
                Error("PACKTHEM_VERSION/1 isn't the 2-byte value 4 that win10 uxtheme requires");
            else Check("PACKTHEM_VERSION/1 is 2 bytes, value 4.");
        }

        private void CheckDwm(SemanticModel tm, SemanticModel cm)
        {
            const string dwm = Win7StyleImporter.DwmClass;
            var cAtlasE = cm.Entries.FirstOrDefault(e => e.ClassName == dwm && e.PartId == 0 && e.TypeId == (int)IDENTIFIER.DISKSTREAM);
            var tAtlasE = tm.Entries.FirstOrDefault(e => e.ClassName == dwm && e.PartId == 0 && e.TypeId == (int)IDENTIFIER.DISKSTREAM);
            if (cAtlasE == null || tAtlasE == null)
            {
                Error("DWMWindow IMAGEATLAS missing");
                return;
            }
            var cBytes = cm.GetResourceBytes("STREAM", cAtlasE.ResourceId);
            var tBytes = tm.GetResourceBytes("STREAM", tAtlasE.ResourceId);
            ArgbImage cAtlas, tAtlas;
            try
            {
                cAtlas = ImageIdentity.Decode(cBytes);
                tAtlas = ImageIdentity.Decode(tBytes);
            }
            catch (Exception ex)
            {
                Error("DWMWindow atlas doesn't decode: " + ex.Message);
                return;
            }

            int rects = 0;
            foreach (var ce in cm.Entries.Where(e => e.ClassName == dwm && e.NameId == (int)IDENTIFIER.ATLASRECT && e.TypeId == (int)IDENTIFIER.RECTTYPE))
            {
                tm.ByKey.TryGetValue(ce.Key, out var te);
                var r = DwmImport.RectOf(ce);
                var tr = te == null ? Rectangle.Empty : DwmImport.RectOf(te);
                bool tEmpty = tr.Width <= 0 || tr.Height <= 0;
                if (r.Width <= 0 || r.Height <= 0)
                {
                    if (!tEmpty) Error($"DWMWindow {ce.PartName}: rect became empty");
                    continue;
                }
                if (r.X < 0 || r.Y < 0 || r.Right > cAtlas.Width || r.Bottom > cAtlas.Height)
                {
                    Error($"DWMWindow {ce.PartName}: rect {r} outside the {cAtlas.Width}x{cAtlas.Height} atlas");
                    continue;
                }

                var cCls = cm.Style.Classes[ce.ClassId];
                bool fits = DwmImport.CheckGeometry(r, DwmImport.EffectiveCount(cm.Style, cCls, ce.PartId, ce.StateId),
                    DwmImport.EffectiveSizing(cm.Style, cCls, ce.PartId, ce.StateId), out string why);
                if (!fits)
                {
                    var tCls = te == null ? null : tm.Style.Classes[te.ClassId];
                    if (!tEmpty && tCls != null && DwmImport.CheckGeometry(tr, DwmImport.EffectiveCount(tm.Style, tCls, te.PartId, te.StateId),
                            DwmImport.EffectiveSizing(tm.Style, tCls, te.PartId, te.StateId), out _))
                    {
                        if (m_geometryWaivers.Contains(ce.PartName)) Warn($"DWMWindow {ce.PartName}: {why} (kept as the source ships it)");
                        else Error($"DWMWindow {ce.PartName}: {why}");
                    }
                    else
                        Warn($"DWMWindow {ce.PartName}: {why} (template has the same problem)");
                }
                rects++;
            }
            Check($"{rects} DWMWindow atlas rects inside the {cAtlas.Width}x{cAtlas.Height} atlas; vertical frame counts, 1px inset and sizing margins fit (or match the template).");

            int sprites = 0;
            foreach (var s in m_sprites)
            {
                var r = s.OutputRect;
                if (r.Right > cAtlas.Width || r.Bottom > cAtlas.Height)
                {
                    Error($"DWMWindow {s.Part}: expected sprite rect {r} outside the atlas");
                    continue;
                }
                if (!Win7StyleImporter.NearlyEqual(cAtlas.Crop(r), s.Expected, 0))
                    Error($"DWMWindow {s.Part}: atlas pixels at {r} aren't the planned sprite");
                else sprites++;
            }
            if (m_sprites.Count > 0) Check($"{sprites} DWMWindow sprites verified pixel-for-pixel against their source (imported or kept template sprite).");

            // every part uDWM can look up in the template must still exist.
            var tParts = new HashSet<int>(tm.Entries.Where(e => e.ClassName == dwm).Select(e => e.PartId));
            var cParts = new HashSet<int>(cm.Entries.Where(e => e.ClassName == dwm).Select(e => e.PartId));
            foreach (var p in tParts.Where(p => !cParts.Contains(p))) Error($"DWMWindow part {p} disappeared");
            Check($"all {tParts.Count} DWMWindow parts of the template present (incl. dark glyphs 64-87 and caption backgrounds 88-91).");
        }

        private void CheckStrings(SemanticModel tm, SemanticModel cm)
        {
            // every string the template had must still read the same, whatever happened to the buckets.
            var ids = tm.Style.StringTables.SelectMany(t => t.Value.Keys).Distinct().ToList();
            int same = 0;
            foreach (var id in ids)
            {
                string before = tm.ResolveString(id), after = cm.ResolveString(id);
                if (after == null) Error($"string {id} \"{before}\" is gone from the candidate");
                else if (after != before) Error($"string {id} changed from \"{before}\" to \"{after}\"");
                else same++;
            }
            if (ids.Count > 0) Check($"{same} template strings resolve to the same text in the candidate{(m_stringsAdded ? " (tables rewritten for added font strings)" : "")}.");
            CheckStringTables(tm, cm);
        }

        /// <summary>
        /// ResolveString falls back across languages and tables, so a bucket that was dropped, merged into
        /// another language or written with the wrong text still "resolves". Compare every language's table
        /// entry by entry instead: a standalone save rewrites the buckets from the loaded tables, so the text
        /// has to survive per language even though the bucket bytes don't.
        /// </summary>
        private void CheckStringTables(SemanticModel tm, SemanticModel cm)
        {
            int compared = 0;
            foreach (var lang in tm.Style.StringTables.Keys.OrderBy(k => k))
            {
                var before = tm.Style.StringTables[lang];
                if (before.Count == 0) continue; // a language the loader enumerated but that holds no text
                if (!cm.Style.StringTables.TryGetValue(lang, out var after))
                {
                    Error($"string table for language {lang} is missing from the candidate");
                    continue;
                }
                int lost = 0, changed = 0;
                foreach (var kv in before.OrderBy(k => k.Key))
                {
                    compared++;
                    if (!after.TryGetValue(kv.Key, out string now)) { lost++; continue; }
                    if (now != kv.Value) changed++;
                }
                if (lost > 0) Error($"string table for language {lang} lost {lost} of {before.Count} strings");
                if (changed > 0) Error($"string table for language {lang} has {changed} strings with different text");
                if (lost == 0 && changed == 0)
                    Check($"string table for language {lang}: all {before.Count} strings kept their text.");
                var extra = after.Keys.Except(before.Keys).ToList();
                if (extra.Count > 0 && !m_stringsAdded)
                    Error($"string table for language {lang} gained {extra.Count} strings the import never added");
            }
            if (compared == 0)
            {
                int cand = cm.Style.StringTables.Sum(t => t.Value.Count);
                Check(cand == 0
                    ? "neither style has string tables; nothing to compare per language."
                    : $"the template has no string table (its strings live in a .mui), so only the candidate's {cand} strings could be read.");
            }
        }

        private void CheckFonts(SemanticModel tm, SemanticModel cm)
        {
            int fonts = 0;
            foreach (var ce in cm.Entries.Where(e => e.TypeId == (int)IDENTIFIER.FONT))
            {
                string text = cm.ResolveString(ce.Property.Header.shortFlag);
                if (text == null) continue; // reported by CheckReferences
                tm.ByKey.TryGetValue(ce.Key, out var te);
                bool changed = te == null || te.RawValue != ce.RawValue;
                string label = $"{ce.ClassName}|{ce.PartName}|S{ce.StateId}|{ce.PropertyName}";
                var f = FontSpec.Parse(text);
                if (f.Error != null)
                {
                    // uxtheme 19041 _ParseFont fails the whole theme load on this.
                    if (changed) Error($"{label}: \"{text}\": {f.Error}");
                    else Warn($"{label}: \"{text}\": {f.Error} (template has it too)");
                    continue;
                }
                if (!f.IsSane)
                {
                    if (changed) Error($"{label}: \"{text}\" isn't a usable font ({string.Join("; ", f.Warnings)})");
                    else Warn($"{label}: \"{text}\": {string.Join("; ", f.Warnings)} (template has it too)");
                    continue;
                }
                fonts++;
            }
            Check($"{fonts} font properties parse the way uxtheme reads them (known style words only, face and nonzero size).");
        }

        // sysmetrics font -> the bar its text sits in. uxtheme copies both into NONCLIENTMETRICS
        // without checking one against the other (CThemeLoader::PackMetrics 0x171B41E2C).
        private static readonly Tuple<IDENTIFIER, IDENTIFIER>[] FontBars =
        {
            Tuple.Create(IDENTIFIER.CAPTIONFONT, IDENTIFIER.CAPTIONBARHEIGHT),
            Tuple.Create(IDENTIFIER.SMALLCAPTIONFONT, IDENTIFIER.SMCAPTIONBARHEIGHT),
            Tuple.Create(IDENTIFIER.MENUFONT, IDENTIFIER.MENUBARHEIGHT),
        };

        /// <summary>
        /// A caption/menu font taller than its bar gets clipped in every window. Both values are
        /// scaled by the same dpi factor, so comparing at 96 dpi is enough. Only an error when the
        /// template's own pair fit.
        /// </summary>
        private void CheckMetrics(SemanticModel tm, SemanticModel cm)
        {
            int pairs = 0;
            foreach (var fb in FontBars)
            {
                var c = MetricPair(cm, fb.Item1, fb.Item2);
                if (c == null) continue;
                pairs++;
                if (c.Item1 <= c.Item2) continue;
                var t = MetricPair(tm, fb.Item1, fb.Item2);
                string msg = $"sysmetrics {fb.Item1} is {c.Item1}px tall at 96 dpi but {fb.Item2} is only {c.Item2}px";
                if (t != null && t.Item1 > t.Item2) Warn(msg + " (template has the same mismatch)");
                else Error(msg + "; the text would be clipped");
            }
            if (pairs > 0) Check($"{pairs} sysmetrics font/bar pairs: each font fits its bar height.");
        }

        private static Tuple<int, int> MetricPair(SemanticModel m, IDENTIFIER font, IDENTIFIER bar)
        {
            var fe = m.Entries.FirstOrDefault(e => e.ClassName.Equals("sysmetrics", StringComparison.OrdinalIgnoreCase) && e.NameId == (int)font && e.TypeId == (int)IDENTIFIER.FONT);
            var be = m.Entries.FirstOrDefault(e => e.ClassName.Equals("sysmetrics", StringComparison.OrdinalIgnoreCase) && e.NameId == (int)bar);
            if (fe == null || be == null || !(be.Property.GetValue() is int height)) return null;
            string text = m.ResolveString(fe.Property.Header.shortFlag);
            var f = text == null ? null : FontSpec.Parse(text);
            if (f == null || !f.IsSane) return null;
            return Tuple.Create(Math.Abs(f.PixelHeight96), height);
        }
    }
}
