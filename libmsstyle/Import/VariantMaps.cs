using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace libmsstyle.Import
{
    /// <summary>
    /// The pieces of a win10 .msstyles that say which color schemes and sizes it holds and what they are called,
    /// worked out against uxtheme 19041 (docs/xp-importer.md, "Color and size variants"):
    /// <list type="bullet">
    /// <item>VMAP: (variant resource name, size name, color name) triples, each string an int32 count of wchars
    ///   (terminator included) and the wchars, padded to 8 bytes. LoaderLoadTheme picks the VARIANT resource whose
    ///   triple matches the requested color and size.</item>
    /// <item>CMAP classes "sizevariant.X" / "colorvariant.X": EnumThemeSize/EnumThemeColors walk the sizevariant /
    ///   colorvariant classes in CMAP order (index = ordinal among them) and return X as the name.</item>
    /// <item>RMAP: 32-byte records (name id, type 201, class index, part, state, string id, 0, inline byte count) with
    ///   an inline UTF-16 string or a string table id. Name 601 is the display name, 602 the tooltip; class 0
    ///   (documentation) 601 is the theme's DisplayName, a variant class's 601 that variant's display name.</item>
    /// </list>
    /// </summary>
    public class VariantMaps
    {
        public const int DisplayNameId = 601, TooltipId = 602;

        public List<string> Classes { get; } = new List<string>();
        public List<int> BaseClasses { get; } = new List<int>();
        public bool BcmapHasCount { get; private set; }
        public List<(string Variant, string Size, string Color)> Variants { get; } = new List<(string, string, string)>();
        public List<RmapRecord> Rmap { get; } = new List<RmapRecord>();
        private byte[] m_bcmapTail = new byte[0];

        public class RmapRecord
        {
            public int Name, Type = 201, Class, Part, State, StringId, Reserved;
            /// <summary>inline text, or null when the record points at <see cref="StringId"/>.</summary>
            public string Text;
        }

        public static VariantMaps Read(byte[] cmap, byte[] bcmap, byte[] vmap, byte[] rmap)
        {
            var m = new VariantMaps();
            for (int i = 0; i < cmap.Length;)
            {
                int e = i;
                while (e + 1 < cmap.Length && (cmap[e] | cmap[e + 1]) != 0) e += 2;
                if (e > i) m.Classes.Add(Encoding.Unicode.GetString(cmap, i, e - i));
                i = (e + 2 + 7) & ~7;
            }

            if (bcmap != null && bcmap.Length >= 4)
            {
                int count = BitConverter.ToInt32(bcmap, 0);
                m.BcmapHasCount = count >= 0 && 4 + count * 4 <= bcmap.Length;
                int start = m.BcmapHasCount ? 4 : 0, n = m.BcmapHasCount ? count : bcmap.Length / 4;
                for (int k = 0; k < n; ++k) m.BaseClasses.Add(BitConverter.ToInt32(bcmap, start + 4 * k));
                int used = start + n * 4;
                m.m_bcmapTail = bcmap.Skip(used).ToArray();
            }

            var strings = new List<string>();
            for (int i = 0; i + 4 <= vmap.Length;)
            {
                int n = BitConverter.ToInt32(vmap, i);
                if (n <= 0 || i + 4 + n * 2 > vmap.Length) throw new InvalidDataException("VMAP string runs past the resource");
                strings.Add(Encoding.Unicode.GetString(vmap, i + 4, (n - 1) * 2));
                i += 4 + ((n * 2 + 7) & ~7);
            }
            if (strings.Count % 3 != 0) throw new InvalidDataException($"VMAP holds {strings.Count} strings, not triples");
            for (int k = 0; k < strings.Count; k += 3) m.Variants.Add((strings[k], strings[k + 1], strings[k + 2]));

            for (int i = 0; i + 32 <= rmap.Length;)
            {
                var h = Enumerable.Range(0, 8).Select(k => BitConverter.ToInt32(rmap, i + 4 * k)).ToArray();
                var r = new RmapRecord { Name = h[0], Type = h[1], Class = h[2], Part = h[3], State = h[4], StringId = h[5], Reserved = h[6] };
                int len = h[7];
                if (r.StringId == 0 && len > 0)
                {
                    if (i + 32 + len > rmap.Length) throw new InvalidDataException("RMAP inline string runs past the resource");
                    r.Text = Encoding.Unicode.GetString(rmap, i + 32, len).TrimEnd('\0');
                    i += 32 + ((len + 7) & ~7);
                }
                else i += 32;
                m.Rmap.Add(r);
            }
            return m;
        }

        public byte[] WriteCmap()
        {
            var ms = new MemoryStream();
            for (int k = 0; k < Classes.Count; ++k)
            {
                var b = Encoding.Unicode.GetBytes(Classes[k] + "\0");
                ms.Write(b, 0, b.Length);
                if (k < Classes.Count - 1) while (ms.Length % 8 != 0) ms.WriteByte(0);
            }
            return ms.ToArray();
        }

        public byte[] WriteBcmap()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            if (BcmapHasCount) w.Write(BaseClasses.Count);
            foreach (var b in BaseClasses) w.Write(b);
            w.Write(m_bcmapTail);
            return ms.ToArray();
        }

        public byte[] WriteVmap()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            foreach (var s in Variants.SelectMany(v => new[] { v.Variant, v.Size, v.Color }))
            {
                var b = Encoding.Unicode.GetBytes(s + "\0");
                w.Write(s.Length + 1);
                w.Write(b);
                for (int p = b.Length; p % 8 != 0; ++p) ms.WriteByte(0);
            }
            return ms.ToArray();
        }

        public byte[] WriteRmap()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            foreach (var r in Rmap)
            {
                byte[] body = r.Text == null ? new byte[0] : Encoding.Unicode.GetBytes(r.Text + "\0");
                foreach (var v in new[] { r.Name, r.Type, r.Class, r.Part, r.State, r.Text == null ? r.StringId : 0, r.Reserved, body.Length }) w.Write(v);
                w.Write(body);
                while (ms.Length % 8 != 0) ms.WriteByte(0);
            }
            return ms.ToArray();
        }

        /// <summary>sets (or adds) an inline RMAP string for a class index.</summary>
        public void SetText(int classIndex, int name, string text)
        {
            var r = Rmap.FirstOrDefault(x => x.Class == classIndex && x.Name == name && x.Part == 0 && x.State == 0);
            if (r == null)
            {
                r = new RmapRecord { Name = name, Class = classIndex };
                // records stay grouped by class, the way packthem writes them.
                int at = Rmap.FindLastIndex(x => x.Class <= classIndex) + 1;
                Rmap.Insert(at, r);
            }
            r.Text = text;
            r.StringId = 0;
        }

        public int ClassIndex(string name) => Classes.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

        public string ColorName => Variants.Count > 0 ? Variants[0].Color : "NormalColor";
    }
}
