using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace libmsstyle.Import
{
    /// <summary>
    /// A theme font string parsed the way uxtheme 10.0.19041 `_ParseFont` reads it
    /// (0x171B59A6C, see docs/win7-importer.md): "Face, size, Bold Italic Underline Strikeout Quality:X".
    /// Used to compare fonts by meaning instead of spelling, and to refuse strings uxtheme would
    /// reject: an unknown style word fails the whole theme load with 0x80070648.
    /// </summary>
    public class FontSpec : IEquatable<FontSpec>
    {
        // uxtheme copies the face into LOGFONT.lfFaceName (32 wchars incl. NUL) and reads the
        // resource string into a 128 wchar buffer.
        public const int MaxFaceLength = 31;
        public const int MaxStringLength = 127;

        public string Face { get; private set; }
        public int Points { get; private set; }
        public bool Bold { get; private set; }
        public bool Italic { get; private set; }
        public bool Underline { get; private set; }
        public bool Strikeout { get; private set; }
        /// <summary>LOGFONT quality: 0 default, 3 nonantialiased, 4 antialiased, 5 cleartype, 6 cleartype-natural.</summary>
        public int Quality { get; private set; }
        /// <summary>LOGFONT lfCharSet. XP has no per-font charset: themeldr parser.cpp keeps one
        /// [globals] Charset (_uCharSet, DEFAULT_CHARSET when absent) and stamps it into every LOGFONT
        /// (ParseFontValue), so the importer puts the style's value here. It only shows up in
        /// <see cref="ToString"/> when it isn't DEFAULT_CHARSET, which keeps every ordinary font string
        /// byte-identical to what uxtheme expects.</summary>
        public int Charset { get; set; } = 1;
        /// <summary>null when uxtheme would accept the string; otherwise why it wouldn't.</summary>
        public string Error { get; private set; }
        /// <summary>accepted by uxtheme but almost certainly not what the author meant (size 0, ignored styles...).</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>height in pixels at 96 dpi, as uxtheme stores it: MulDiv(points, 96, 72).</summary>
        public int PixelHeight96 => (int)Math.Round(Points * 96.0 / 72.0, MidpointRounding.AwayFromZero);

        private static readonly Dictionary<string, int> Qualities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "quality:cleartype", 5 },
            { "quality:cleartype-natural", 6 },
            { "quality:antialiased", 4 },
            { "quality:nonantialiased", 3 },
        };

        public static FontSpec Parse(string text)
        {
            var f = new FontSpec();
            if (text == null) { f.Error = "no string"; return f; }
            if (text.Length > MaxStringLength) f.Warnings.Add($"longer than {MaxStringLength} characters, uxtheme cuts it off");

            // face: up to the first comma, colon or semicolon, whitespace kept as is.
            int end = text.IndexOfAny(new[] { ',', ':', ';' });
            string face = end < 0 ? text : text.Substring(0, end);
            if (face.Length > MaxFaceLength)
            {
                f.Warnings.Add($"face name longer than {MaxFaceLength} characters, uxtheme truncates it");
                face = face.Substring(0, MaxFaceLength);
            }
            f.Face = face;
            if (face.Trim().Length == 0) f.Warnings.Add("empty face name");
            if (end < 0) { f.Warnings.Add("no size"); return f; }

            string rest = text.Substring(end + 1);
            int comma = rest.IndexOf(',');
            string sizeToken = (comma < 0 ? rest : rest.Substring(0, comma)).Trim();
            string styles = comma < 0 ? "" : rest.Substring(comma + 1);

            if (sizeToken.Length == 0 || !(char.IsDigit(sizeToken[0]) || sizeToken[0] == '-' || sizeToken[0] == '+'))
            {
                // uxtheme returns success right here and ignores everything after it.
                f.Warnings.Add("no numeric size, uxtheme stops parsing (height 0, styles ignored)");
                return f;
            }
            f.Points = ParseNumber(sizeToken, out bool trailing);
            if (trailing) f.Warnings.Add($"size \"{sizeToken}\" isn't a plain integer, uxtheme reads {f.Points}");
            // a negative size is a pixel height: themeldr parser.cpp ParseSizeInfoUnits stores -MulDiv(points, 96, 72), so
            // "-1" is lfHeight +1, a 1px cell. XPMC hides the Start button's text that way ("Arial, -1").
            if (f.Points == 0) f.Warnings.Add("size 0 isn't a usable point size");

            foreach (var tok in styles.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.Equals("bold", StringComparison.OrdinalIgnoreCase)) f.Bold = true;
                else if (tok.Equals("italic", StringComparison.OrdinalIgnoreCase)) f.Italic = true;
                else if (tok.Equals("underline", StringComparison.OrdinalIgnoreCase)) f.Underline = true;
                else if (tok.Equals("strikeout", StringComparison.OrdinalIgnoreCase)) f.Strikeout = true;
                else if (Qualities.TryGetValue(tok, out int q)) f.Quality = q;
                else if (tok.StartsWith("charset:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(tok.Substring("charset:".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cs))
                    f.Charset = cs;
                else
                {
                    f.Error = $"unknown font style \"{tok}\"; uxtheme refuses to load the whole theme over it";
                    return f;
                }
            }
            return f;
        }

        // optional sign, decimal or 0x hex, stops at the first character that doesn't fit.
        private static int ParseNumber(string s, out bool trailing)
        {
            int i = 0, sign = 1;
            if (s[i] == '-' || s[i] == '+') { if (s[i] == '-') sign = -1; i++; }
            bool hex = i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X');
            if (hex) i += 2;
            long v = 0;
            int start = i;
            for (; i < s.Length; ++i)
            {
                int d = hex ? Uri.IsHexDigit(s[i]) ? Uri.FromHex(s[i]) : -1 : char.IsDigit(s[i]) ? s[i] - '0' : -1;
                if (d < 0) break;
                v = unchecked(v * (hex ? 16 : 10) + d);
            }
            trailing = i < s.Length;
            return unchecked((int)(v * sign));
        }

        /// <summary>usable as a theme font: parses, nonzero size, has a face.</summary>
        public bool IsSane => Error == null && Points != 0 && Face.Trim().Length > 0;

        public bool Equals(FontSpec o)
        {
            // GDI matches face names without regard to case.
            return o != null && Error == null && o.Error == null
                && string.Equals(Face, o.Face, StringComparison.OrdinalIgnoreCase)
                && Points == o.Points && Bold == o.Bold && Italic == o.Italic
                && Underline == o.Underline && Strikeout == o.Strikeout && Quality == o.Quality
                && Charset == o.Charset;
        }

        public override bool Equals(object obj) => Equals(obj as FontSpec);
        public override int GetHashCode() => (Face ?? "").ToLowerInvariant().GetHashCode() ^ Points ^ (Charset << 24);

        public override string ToString()
        {
            var parts = new List<string> { Face, Points.ToString(CultureInfo.InvariantCulture) };
            if (Bold) parts.Add("Bold");
            if (Italic) parts.Add("Italic");
            if (Underline) parts.Add("Underline");
            if (Strikeout) parts.Add("Strikeout");
            var q = Qualities.FirstOrDefault(kv => kv.Value == Quality).Key;
            if (q != null) parts.Add(q == "quality:cleartype" ? "Quality:ClearType" : q);
            // only a non-default charset is written out: a plain "Tahoma, 8" has to stay byte
            // identical to what XP wrote, since uxtheme reads these strings back.
            if (Charset != 1) parts.Add("Charset:" + Charset.ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", parts);
        }
    }
}
