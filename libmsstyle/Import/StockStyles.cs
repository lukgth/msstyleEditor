using System;
using System.IO;
using System.Linq;

namespace libmsstyle.Import
{
    /// <summary>
    /// Stock styles compiled into libmsstyle so the importers only need the custom style: windows 7
    /// aero (base of win7 imports) and windows xp luna (base of xp imports). VisualStyle loads
    /// through LoadLibraryEx, which wants a file, so the bytes are written to %TEMP% once and reused.
    /// </summary>
    public static class StockStyles
    {
        public static string Win7AeroPath() => Extract("libmsstyle.Import.w7_aero.msstyles", "w7_aero", "stock win7 aero", ".msstyles");

        public static string XpLunaPath() => Extract("libmsstyle.Import.xp_luna.msstyles", "xp_luna", "stock xp luna", ".msstyles");

        /// <summary>stock xp luna Shell\&lt;scheme&gt;\shellstyle.dll: NormalColor, Metallic or HomeStead.</summary>
        public static string XpLunaShellPath(string scheme)
        {
            string s = (scheme ?? "NormalColor").ToLowerInvariant();
            if (s != "normalcolor" && s != "metallic" && s != "homestead")
                throw new ArgumentException($"stock luna has no shellstyle for scheme \"{scheme}\" (NormalColor, Metallic, HomeStead)");
            return Extract($"libmsstyle.Import.xp_luna_shell_{s}.dll", "xp_luna_shell_" + s, "stock xp luna shellstyle " + s, ".dll");
        }

        /// <summary>
        /// xp's %SystemRoot%\System32\shellstyle.dll: shell32's SHGetShellStyleHInstance loads it when the
        /// theme has no Shell\&lt;color&gt;\shellstyle.dll. Its colors are system color names, so it follows the theme's SysMetrics.
        /// </summary>
        public static string XpClassicShellPath() => Extract("libmsstyle.Import.xp_classic_shell.dll", "xp_classic_shell", "xp classic shellstyle", ".dll");

        private static string Extract(string resourceName, string fileStem, string what, string extension = ".msstyles")
        {
            using (var s = typeof(StockStyles).Assembly.GetManifestResourceStream(resourceName))
            {
                if (s == null) throw new FileNotFoundException(what + " isn't embedded in this libmsstyle build");
                var ms = new MemoryStream();
                s.CopyTo(ms);
                byte[] data = ms.ToArray();

                // hash in the name: a different embedded copy never reuses a stale file.
                string sha = ResourceInventory.Sha1Hex(data).Substring(0, 12);
                string dir = Path.Combine(Path.GetTempPath(), "msstyleEditor");
                string path = Path.Combine(dir, $"{fileStem}-{sha}{extension}");
                if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(data)) return path;

                Directory.CreateDirectory(dir);
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, data);
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                catch (IOException)
                {
                    // another instance won the race and may have it mapped; its copy is identical.
                    File.Delete(tmp);
                    if (!File.Exists(path)) throw;
                }
                return path;
            }
        }
    }
}
