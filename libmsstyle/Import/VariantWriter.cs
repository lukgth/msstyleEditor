using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace libmsstyle.Import
{
    /// <summary>
    /// Gives a ported style its xp names and sizes (<see cref="VariantMaps"/> for the resource layout):
    /// <list type="bullet">
    /// <item>the documentation DisplayName becomes the xp theme's name ("Default (blue)" in every Luna10-based port);</item>
    /// <item>the color variant is renamed to the scheme's internal name with its display name ("Blue", "Green"), so
    ///   Display Properties lists it under the scheme's name;</item>
    /// <item>every other xp size (Large Fonts, Extra Large Fonts) becomes a VARIANT resource of its own, built from a
    ///   port of that size's INI, with a "sizevariant.&lt;size&gt;" class, a VMAP triple and its display name, so
    ///   Font size offers it.</item>
    /// </list>
    /// </summary>
    public static class VariantWriter
    {
        public class Size
        {
            public string Name;         // "LargeFonts"
            public string DisplayName;  // "Large Fonts"
            /// <summary>a port of that size built from the same template and options as the main output.</summary>
            public string PortPath;
        }

        /// <param name="stylePath">the NormalSize port, rewritten in place.</param>
        /// <param name="sizes">ports of the other sizes, from the same template. Their IMAGE/STREAM resources and font
        /// strings are matched to the main output by content; what's new is added under fresh ids and the size's
        /// VARIANT records are pointed at those.</param>
        /// <param name="normalSizeDisplayName">Font size label of the default size; null keeps the template's "Normal".</param>
        /// <returns>one line per check the rewrite passed, for the caller's report; a failed check throws.</returns>
        public static List<string> Apply(string stylePath, string themeName, string colorName, string colorDisplayName, IList<Size> sizes,
            string normalSizeDisplayName = null)
        {
            byte[] cmap, bcmap, vmap, rmap;
            Dictionary<int, string> mainStrings;
            Dictionary<int, Dictionary<int, string>> mainTables;
            ResourceIdAllocator alloc;
            List<int> registration;
            using (var inv = new ResourceInventory(stylePath))
            using (var main = new VisualStyle())
            {
                byte[] Get(string type) => inv.Entries.Where(e => e.Type == type && e.Name == type).Select(inv.GetData).FirstOrDefault();
                cmap = Get("CMAP"); bcmap = Get("BCMAP"); vmap = Get("VMAP"); rmap = Get("RMAP");
                main.Load(stylePath);
                registration = Win7StyleImporter.RegistrationOrder(main);
                mainStrings = Flatten(main);
                // each language's own table, kept apart: a merged table written into every language would replace
                // each language's localized strings with the lowest language's text.
                mainTables = main.StringTables.ToDictionary(t => t.Key, t => new Dictionary<int, string>(t.Value));
                // only resources with a numeric name can be reused: a name like "foo" isn't an id, and recording it as
                // 0 would make a later Place hand out 0 (which a property header reads as "inline payload").
                IEnumerable<Tuple<string, int, string>> Existing()
                {
                    foreach (var e in inv.Entries.Where(e => e.Type == "IMAGE" || e.Type == "STREAM"))
                        if (e.TryGetIntName(out int id)) yield return Tuple.Create(e.Type, id, e.Sha1);
                }
                alloc = new ResourceIdAllocator(Existing());
            }
            if (cmap == null || vmap == null || rmap == null) throw new InvalidDataException($"\"{stylePath}\" has no CMAP/VMAP/RMAP");
            var m = VariantMaps.Read(cmap, bcmap, vmap, rmap);
            if (m.Variants.Count != 1) throw new InvalidDataException($"\"{stylePath}\" already holds {m.Variants.Count} variants");

            // documentation 601 = the theme's DisplayName (uxtheme GetThemeDocumentationProperty "DisplayName").
            int doc = m.ClassIndex("documentation");
            if (doc >= 0 && !string.IsNullOrWhiteSpace(themeName)) m.SetText(doc, VariantMaps.DisplayNameId, themeName);

            // the color variant: class colorvariant.<old> -> colorvariant.<colorName>, VMAP color column the same.
            string oldColor = m.ColorName;
            int ci = m.ClassIndex("colorvariant." + oldColor);
            if (ci < 0) throw new InvalidDataException($"\"{stylePath}\" has no class colorvariant.{oldColor}");
            m.Classes[ci] = "colorvariant." + colorName;
            m.SetText(ci, VariantMaps.DisplayNameId, colorDisplayName);
            m.SetText(ci, VariantMaps.TooltipId, colorDisplayName);
            var v0 = m.Variants[0];
            m.Variants[0] = (v0.Variant, v0.Size, colorName);
            int si = m.ClassIndex("sizevariant." + v0.Size);
            if (si >= 0 && !string.IsNullOrWhiteSpace(normalSizeDisplayName))
            {
                m.SetText(si, VariantMaps.DisplayNameId, normalSizeDisplayName);
                m.SetText(si, VariantMaps.TooltipId, normalSizeDisplayName);
            }

            // new size classes go to the end of CMAP so no class index in any VARIANT moves. They carry no records,
            // which uxtheme accepts for variant classes (Luna10's sizevariant.Default has none either). BCMAP has one
            // entry per class after the 4 leading special ones, so each new class appends "no base" there.
            var variants = new List<(string Name, byte[] Data)>();
            var added = new List<(string Type, int Id, byte[] Data)>();
            var addedStrings = new Dictionary<int, string>();
            int nextString = mainStrings.Keys.DefaultIfEmpty(0).Max() + 1;
            foreach (var s in sizes)
            {
                using (var port = new VisualStyle())
                {
                    port.Load(s.PortPath);
                    var portStrings = Flatten(port);
                    // Independently ported sizes can append source classes after different metadata classes.
                    // Match every class instance by name/occurrence, never assume its numeric CMAP ID agrees.
                    var classIds = new Dictionary<int, int>();
                    var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var cls in port.Classes.Values.OrderBy(c => c.ClassId))
                    {
                        occurrences.TryGetValue(cls.ClassName, out int occurrence);
                        occurrences[cls.ClassName] = occurrence + 1;
                        string className = cls.ClassName.Equals("colorvariant." + oldColor, StringComparison.OrdinalIgnoreCase)
                            ? "colorvariant." + colorName : cls.ClassName;
                        int id = m.Classes.Select((name, index) => new { name, index })
                            .Where(x => x.name.Equals(className, StringComparison.OrdinalIgnoreCase)).Skip(occurrence).Select(x => x.index).DefaultIfEmpty(-1).First();
                        if (id < 0) throw new InvalidDataException($"size port has class {cls.ClassName} instance {occurrence + 1} absent from the main port");
                        classIds.Add(cls.ClassId, id);
                    }
                    if (!Win7StyleImporter.RegistrationOrder(port).Select(id => classIds[id]).SequenceEqual(registration))
                        throw new InvalidDataException($"size port {s.Name} changes the registered class order and cannot share the main BCMAP");
                    var ids = new Dictionary<(string, int), int>();
                    foreach (var prop in port.Classes.Values.SelectMany(c => c.Parts.Values).SelectMany(p => p.States.Values).SelectMany(st => st.Properties))
                    {
                        prop.Header.classID = classIds[prop.Header.classID];
                        var res = SemanticModel.ResourceRef(prop, Platform.Win10);
                        if (res != null)
                        {
                            if (!ids.TryGetValue((res.Item1, res.Item2), out int nid))
                            {
                                var data = port.GetRawResource(res.Item1, res.Item2);
                                if (data == null) throw new InvalidDataException($"\"{s.PortPath}\" points at a missing {res.Item1} {res.Item2}");
                                nid = alloc.Place(res.Item1, data, out bool reused);
                                if (!reused) added.Add((res.Item1, nid, data));
                                ids[(res.Item1, res.Item2)] = nid;
                            }
                            prop.Header.shortFlag = (int)((uint)prop.Header.shortFlag & 0xFFFF0000u) | nid;
                        }
                        else if (SemanticModel.IsStringRef(prop, Platform.Win10) && portStrings.TryGetValue(prop.Header.shortFlag, out var text)
                            && !(mainStrings.TryGetValue(prop.Header.shortFlag, out var same) && same == text))
                        {
                            // the main output's id when it has the text, else a new id (this size's fonts).
                            int sid = mainStrings.Concat(addedStrings).Where(kv => kv.Value == text).Select(kv => kv.Key).DefaultIfEmpty(-1).First();
                            if (sid < 0) { sid = nextString++; addedStrings[sid] = text; }
                            prop.Header.shortFlag = sid;
                        }
                    }
                    variants.Add(("S_" + s.Name.ToUpperInvariant(), port.SerializeProperties()));
                }
                int idx = m.Classes.Count;
                m.Classes.Add("sizevariant." + s.Name);
                if (bcmap != null) m.BaseClasses.Add(-1);
                m.SetText(idx, VariantMaps.DisplayNameId, s.DisplayName);
                m.SetText(idx, VariantMaps.TooltipId, s.DisplayName);
                m.Variants.Add(("S_" + s.Name.ToUpperInvariant(), s.Name, colorName));
            }

            IntPtr mod = Win32Api.LoadLibraryEx(stylePath, IntPtr.Zero, Win32Api.LoadLibraryFlags.LOAD_LIBRARY_AS_DATAFILE);
            if (mod == IntPtr.Zero) throw new IOException($"can't open \"{stylePath}\"");
            IntPtr h = Win32Api.BeginUpdateResource(stylePath, false);
            if (h == IntPtr.Zero) { Win32Api.FreeLibrary(mod); throw new IOException($"can't open \"{stylePath}\" for resource updates"); }
            bool ok = Update(h, "CMAP", m.WriteCmap()) && Update(h, "VMAP", m.WriteVmap()) && Update(h, "RMAP", m.WriteRmap())
                && (bcmap == null || Update(h, "BCMAP", m.WriteBcmap()));
            foreach (var (name, data) in variants) ok = ok && Win32Api.UpdateResource(h, "VARIANT", name, 0, data, (uint)data.Length);
            foreach (var a in added) ok = ok && Win32Api.UpdateResource(h, a.Type, (uint)a.Id, 0, a.Data, (uint)a.Data.Length);
            if (ok && addedStrings.Count > 0)
            {
                // string table buckets are written whole, so each language gets its own table: the main output's
                // table for that language plus the size ports' fonts. Writing one merged table into every language
                // would replace every language's localized text with the lowest language's.
                foreach (var lang in mainTables.Keys.Where(k => k >= 0 && k <= ushort.MaxValue).Select(k => (ushort)k).DefaultIfEmpty((ushort)0))
                {
                    var table = mainTables.TryGetValue(lang, out var t) ? t : new Dictionary<int, string>();
                    foreach (var kv in addedStrings) table[kv.Key] = kv.Value;
                    ok = ok && ResourceAccess.StringTable.Update(mod, h, table, lang);
                }
            }
            int err = Marshal.GetLastWin32Error();
            Win32Api.FreeLibrary(mod);
            if (!Win32Api.EndUpdateResource(h, !ok) || !ok) throw new IOException($"writing the variants into \"{stylePath}\" failed (error {err})");
            Signature.AppendSignature(stylePath, VisualStyle.PackthemSignature);
            return Verify(stylePath, m);
        }

        /// <summary>
        /// Reads the rewritten file back and makes uxtheme parse every variant: the maps and the variant streams are
        /// written here, after the importer's own validation ran, so nothing else would notice a rewrite that reads
        /// back differently from what was written. Throws on the first disagreement.
        /// </summary>
        private static List<string> Verify(string stylePath, VariantMaps written)
        {
            var checks = new List<string>();
            using (var inv = new ResourceInventory(stylePath))
            {
                byte[] Get(string type) => inv.Entries.Where(e => e.Type == type && e.Name == type).Select(inv.GetData).FirstOrDefault();
                byte[] cmap = Get("CMAP"), bcmap = Get("BCMAP"), vmap = Get("VMAP"), rmap = Get("RMAP");
                if (cmap == null || vmap == null || rmap == null) throw new InvalidDataException($"\"{stylePath}\" lost its CMAP/VMAP/RMAP in the variant rewrite");
                var back = VariantMaps.Read(cmap, bcmap, vmap, rmap);
                if (!back.Classes.SequenceEqual(written.Classes))
                    throw new InvalidDataException($"\"{stylePath}\": the rewritten CMAP reads back as {back.Classes.Count} classes, not the {written.Classes.Count} written");
                if (written.BaseClasses.Count > 0 && !back.BaseClasses.SequenceEqual(written.BaseClasses))
                    throw new InvalidDataException($"\"{stylePath}\": the rewritten BCMAP reads back as {back.BaseClasses.Count} entries, not the {written.BaseClasses.Count} written");
                if (!back.Variants.SequenceEqual(written.Variants))
                    throw new InvalidDataException($"\"{stylePath}\": the rewritten VMAP reads back as {back.Variants.Count} variants, not the {written.Variants.Count} written");
                if (!SameRecords(back.Rmap, written.Rmap))
                    throw new InvalidDataException($"\"{stylePath}\": the rewritten RMAP doesn't read back as written");

                // a variant class without a display name lists blank in Display Properties.
                foreach (var v in back.Variants)
                    foreach (string cls in new[] { "sizevariant." + v.Size, "colorvariant." + v.Color })
                    {
                        int idx = back.ClassIndex(cls);
                        if (idx < 0) throw new InvalidDataException($"\"{stylePath}\": {cls} is in the VMAP but not in the CMAP");
                        var name = back.Rmap.FirstOrDefault(x => x.Class == idx && x.Name == VariantMaps.DisplayNameId && x.Part == 0 && x.State == 0);
                        // inline text, or a string table id - either names the variant; neither means it lists blank.
                        if (name == null || (string.IsNullOrEmpty(name.Text) && name.StringId == 0))
                            throw new InvalidDataException($"\"{stylePath}\": {cls} has no display name, so it would list blank");
                    }
                checks.Add($"the rewritten CMAP/VMAP/RMAP{(bcmap == null ? "" : "/BCMAP")} read back as written ({back.Classes.Count} classes, {back.Variants.Count} variants).");

                // the VMAP names the VARIANT resources by string, so a missing one only shows up as a load failure.
                // FindResource compares string resource names case-insensitively, so "Normal" finds "NORMAL".
                foreach (var v in back.Variants)
                    if (!inv.Entries.Any(e => e.Type == "VARIANT" && string.Equals(e.Name, v.Variant, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException($"\"{stylePath}\": the VMAP names VARIANT \"{v.Variant}\", which the file doesn't hold");
            }

            // only uxtheme can say whether the maps, the class table and the variant streams agree.
            foreach (var size in written.Variants.Select(v => v.Size).Distinct())
            {
                var result = UxthemeLoader.TryLoad(stylePath, written.ColorName, size, out string error);
                if (result == UxthemeLoadResult.Failed)
                    throw new InvalidDataException($"uxtheme rejects the rewritten variants for {written.ColorName}/{size}: {error}");
                // a skipped check is not a passed one: it is simply not claimed.
                if (result == UxthemeLoadResult.Loaded)
                    checks.Add($"uxtheme loads {written.ColorName}/{size} after the variant rewrite.");
            }
            return checks;
        }

        private static bool SameRecords(List<VariantMaps.RmapRecord> a, List<VariantMaps.RmapRecord> b)
            => a.Count == b.Count && a.Zip(b, (x, y) => x.Name == y.Name && x.Type == y.Type && x.Class == y.Class && x.Part == y.Part
                && x.State == y.State && x.StringId == y.StringId && x.Reserved == y.Reserved && x.Text == y.Text).All(same => same);

        private static Dictionary<int, string> Flatten(VisualStyle s)
            => s.StringTables.OrderBy(t => t.Key).SelectMany(t => t.Value).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);

        // CMAP/VMAP/RMAP/BCMAP of a packthem style are language neutral.
        private static bool Update(IntPtr h, string type, byte[] data)
            => Win32Api.UpdateResource(h, type, type, 0, data, (uint)data.Length);
    }
}
