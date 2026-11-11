using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace libmsstyle.Import
{
    /// <summary>
    /// A style rarely works as a lone .msstyles: explorer loads Shell\&lt;color&gt;\shellstyle.dll
    /// from next to it, and the string table lives in &lt;lang&gt;\&lt;name&gt;.msstyles.mui. Without the
    /// shell folder windows fails to load the shellstyle when the style is applied.
    /// </summary>
    public static class StyleCompanions
    {
        /// <summary>
        /// Copies the Shell folder and every &lt;lang&gt;\&lt;name&gt;.msstyles.mui next to
        /// <paramref name="newStyle"/>, renaming the .mui to the new style's name.
        /// Returns the copied paths (relative to the new style's folder).
        /// </summary>
        public static List<string> CopyAlongside(string originalStyle, string newStyle)
        {
            var copied = new List<string>();
            string srcDir = Path.GetDirectoryName(Path.GetFullPath(originalStyle));
            string dstDir = Path.GetDirectoryName(Path.GetFullPath(newStyle));
            if (string.Equals(srcDir, dstDir, StringComparison.OrdinalIgnoreCase)) return copied;

            string shell = Path.Combine(srcDir, "Shell");
            if (Directory.Exists(shell))
            {
                CopyDirectory(shell, Path.Combine(dstDir, "Shell"));
                copied.Add("Shell\\");
            }

            string srcMui = Path.GetFileName(originalStyle) + ".mui";
            string dstMui = Path.GetFileName(newStyle) + ".mui";
            foreach (var dir in Directory.GetDirectories(srcDir))
            {
                string lang = Path.GetFileName(dir);
                if (!IsCultureName(lang)) continue;
                string mui = Path.Combine(dir, srcMui);
                if (!File.Exists(mui)) continue;
                Directory.CreateDirectory(Path.Combine(dstDir, lang));
                File.Copy(mui, Path.Combine(dstDir, lang, dstMui), true);
                copied.Add(lang + "\\" + dstMui);
            }
            return copied;
        }

        /// <summary>
        /// The shellstyle folder a style folder ships (Shell\&lt;color&gt;\shellstyle.dll), or null when it has none.
        /// The folder is named after the color variant and a template's name is not always "NormalColor", so the
        /// name is read from the folder instead of assumed.
        /// </summary>
        public static string FindShellFolder(string styleDir)
        {
            string shell = Path.Combine(styleDir, "Shell");
            if (!Directory.Exists(shell)) return null;
            foreach (var dir in Directory.GetDirectories(shell))
                if (File.Exists(Path.Combine(dir, "shellstyle.dll"))) return dir;
            return null;
        }

        private static bool IsCultureName(string name)
        {
            try
            {
                return !string.IsNullOrEmpty(name) && CultureInfo.GetCultureInfo(name) != null;
            }
            catch (CultureNotFoundException)
            {
                return false;
            }
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}
