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
        public static void Apply(string stylePath, string themeName, string colorName, string colorDisplayName, IList<Size> sizes)
        {
            byte[] cmap, bcmap, vmap, rmap;
            Dictionary<int, string> mainStrings;
            ResourceIdAllocator alloc;
            using (var inv = new ResourceInventory(stylePath))
            using (var main = new VisualStyle())
            {
                byte[] Get(string type) => inv.Entries.Where(e => e.Type == type && e.Name == type).Select(inv.GetData).FirstOrDefault();
                cmap = Get("CMAP"); bcmap = Get("BCMAP"); vmap = Get("VMAP"); rmap = Get("RMAP");
                main.Load(stylePath);
                mainStrings = Flatten(main);
                alloc = new ResourceIdAllocator(inv.Entries.Where(e => e.Type == "IMAGE" || e.Type == "STREAM")
                    .Select(e => { e.TryGetIntName(out int id); return Tuple.Create(e.Type, id, e.Sha1); }));
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
                    var ids = new Dictionary<(string, int), int>();
                    foreach (var prop in port.Classes.Values.SelectMany(c => c.Parts.Values).SelectMany(p => p.States.Values).SelectMany(st => st.Properties))
                    {
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
            var langs = ResourceAccess.GetAllLanguageIds(mod, "#" + Win32Api.RT_STRING, 7, Win32Api.EnumResourceFlags.RESOURCE_ENUM_LN)
                .Union(ResourceAccess.GetAllLanguageIds(mod, "#" + Win32Api.RT_STRING, 32, Win32Api.EnumResourceFlags.RESOURCE_ENUM_LN)).ToList();
            IntPtr h = Win32Api.BeginUpdateResource(stylePath, false);
            if (h == IntPtr.Zero) { Win32Api.FreeLibrary(mod); throw new IOException($"can't open \"{stylePath}\" for resource updates"); }
            bool ok = Update(h, "CMAP", m.WriteCmap()) && Update(h, "VMAP", m.WriteVmap()) && Update(h, "RMAP", m.WriteRmap())
                && (bcmap == null || Update(h, "BCMAP", m.WriteBcmap()));
            foreach (var (name, data) in variants) ok = ok && Win32Api.UpdateResource(h, "VARIANT", name, 0, data, (uint)data.Length);
            foreach (var a in added) ok = ok && Win32Api.UpdateResource(h, a.Type, (uint)a.Id, 0, a.Data, (uint)a.Data.Length);
            if (ok && addedStrings.Count > 0)
            {
                // string table buckets are written whole: the main table plus the sizes' fonts, in every language.
                var all = mainStrings.Concat(addedStrings).ToDictionary(kv => kv.Key, kv => kv.Value);
                foreach (var lang in langs.DefaultIfEmpty((ushort)0))
                    ok = ok && ResourceAccess.StringTable.Update(mod, h, all, lang);
            }
            int err = Marshal.GetLastWin32Error();
            Win32Api.FreeLibrary(mod);
            if (!Win32Api.EndUpdateResource(h, !ok) || !ok) throw new IOException($"writing the variants into \"{stylePath}\" failed (error {err})");
            Signature.AppendSignature(stylePath, VisualStyle.PackthemSignature);
        }

        private static Dictionary<int, string> Flatten(VisualStyle s)
            => s.StringTables.OrderBy(t => t.Key).SelectMany(t => t.Value).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);

        // CMAP/VMAP/RMAP/BCMAP of a packthem style are language neutral.
        private static bool Update(IntPtr h, string type, byte[] data)
            => Win32Api.UpdateResource(h, type, type, 0, data, (uint)data.Length);
    }
}
