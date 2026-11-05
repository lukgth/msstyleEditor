using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace libmsstyle.Import
{
    /// <summary>Options for porting a whole xp theme (every chosen color scheme) onto a win10 xp port.</summary>
    public class XpThemeImportOptions
    {
        /// <summary>the custom xp .msstyles.</summary>
        public string SourcePath { get; set; }
        /// <summary>the working win10 xp port the result is built on (e.g. ported Luna.msstyles).</summary>
        public string TemplatePath { get; set; }
        /// <summary>shell folder and .mui come from here; defaults to <see cref="TemplatePath"/>.</summary>
        public string CompanionSourcePath { get; set; }
        public string OutDir { get; set; }
        /// <summary>internal or display names; null = every scheme of the source.</summary>
        public List<string> Schemes { get; set; }
        /// <summary>display name; defaults to the source file name.</summary>
        public string Name { get; set; }
        public string BasePath { get; set; }
        public string BaseScheme { get; set; } = "NormalColor";
        /// <summary>xp shellstyle.dll; default Shell\&lt;scheme&gt;\shellstyle.dll next to the source.</summary>
        public string ShellStylePath { get; set; }
        public string Wallpaper { get; set; } = XpThemeFiles.DefaultWallpaper;
        public bool Write { get; set; }
        public List<string> PreferSource { get; set; } = new List<string>();
        public List<string> Exclude { get; set; } = new List<string>();
        public int? ImageTolerance { get; set; }
    }

    /// <summary>One color scheme's port.</summary>
    public class XpSchemeResult
    {
        public XpColorScheme Scheme { get; set; }
        /// <summary>"&lt;theme&gt; &lt;scheme&gt;": stem of the .theme and report files.</summary>
        public string Label { get; set; }
        public ImportReport Report { get; set; }
        public List<string> ReportFiles { get; set; }
        /// <summary>written .msstyles, null on a dry run or when blocked.</summary>
        public string StylePath { get; set; }
        /// <summary>written .theme, null on a dry run or when blocked.</summary>
        public string ThemePath { get; set; }
        /// <summary>the theme's folder (shared by all its schemes) to copy to %WINDIR%\Resources\Themes.</summary>
        public string StyleFolder { get; set; }
        public bool Ok => Report.Status == ImportStatus.ReadyForVmTest;
    }

    /// <summary>
    /// Ports an xp theme onto a win10 xp port: one folder, one style per color scheme holding every xp size, the layout the
    /// working ports use. Shared by msstyleImport import-xp and the editor's ribbon.
    /// </summary>
    public static class XpThemeImport
    {
        /// <summary>the color schemes of an xp style; throws when it isn't one.</summary>
        public static List<XpColorScheme> ReadSchemes(string path)
        {
            using (var style = new VisualStyle())
            {
                style.Load(path);
                if (style.Platform != Platform.WinXP) throw new ArgumentException($"\"{path}\" is a {style.Platform.ToDisplayString()} style, not windows xp");
                return style.XpColorSchemes.ToList();
            }
        }

        /// <param name="progress">called before each scheme starts; may be null.</param>
        public static List<XpSchemeResult> Run(XpThemeImportOptions o, Action<XpColorScheme, string> progress = null)
        {
            var all = ReadSchemes(o.SourcePath);
            var schemes = o.Schemes == null || o.Schemes.Count == 0 ? all : o.Schemes.Select(w =>
                all.FirstOrDefault(s => s.InternalName.Equals(w, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(s => s.DisplayName.Equals(w, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"source has no color scheme \"{w}\" (has: {string.Join(", ", all.Select(s => s.InternalName))})")).ToList();

            string outDir = Path.GetFullPath(o.OutDir);
            string name = XpThemeFiles.SafeName(o.Name ?? Path.GetFileNameWithoutExtension(o.SourcePath));
            string reportDir = Path.Combine(outDir, "reports");
            Directory.CreateDirectory(reportDir);
            string themeName = o.Name ?? ReadThemeName(o.SourcePath) ?? name;

            // the layout of Luna/Olive/Silver: one folder per theme (Display Properties' "Windows and buttons"), one
            // .msstyles per color scheme ("Color scheme") holding every xp size ("Font size"), Shell\<scheme> next to
            // them. Each scheme's color variant is named after the scheme, so windows loads Shell\<scheme>\shellstyle.dll.
            string styleDir = Path.Combine(outDir, name);
            var results = new List<XpSchemeResult>();
            foreach (var scheme in schemes)
            {
                string label = all.Count == 1 ? name : $"{name} {XpThemeFiles.SafeName(scheme.InternalName)}";
                string file = (all.Count == 1 ? name : XpThemeFiles.SafeName(scheme.InternalName)) + ".msstyles";
                string display = all.Count == 1 ? themeName : $"{themeName} {scheme.DisplayName.Trim()}";
                Win7ImportOptions Options(string size, string output) => new Win7ImportOptions
                {
                    SourcePath = o.SourcePath,
                    SourcePlatform = Platform.WinXP,
                    SourceScheme = scheme.InternalName,
                    SourceSize = size,
                    BasePath = o.BasePath,
                    BaseScheme = o.BaseScheme ?? "NormalColor",
                    TemplatePath = o.TemplatePath,
                    CompanionSourcePath = o.CompanionSourcePath,
                    DryRun = !o.Write,
                    OutputPath = output,
                    SourceShellStylePath = o.ShellStylePath,
                    PreferSource = o.PreferSource ?? new List<string>(),
                    Exclude = o.Exclude ?? new List<string>(),
                    ImageTolerance = o.ImageTolerance ?? 2,
                };

                // every scheme is ported in a folder of its own first; the shared theme folder is assembled after.
                string work = Path.Combine(outDir, ".work", XpThemeFiles.SafeName(scheme.InternalName));
                if (Directory.Exists(work)) Directory.Delete(work, true);
                progress?.Invoke(scheme, file);
                var opt = Options(null, Path.Combine(work, "normal", file));
                var report = Win7StyleImporter.Run(opt);
                var res = new XpSchemeResult
                {
                    Scheme = scheme,
                    Label = label,
                    Report = report,
                    StyleFolder = styleDir,
                };
                results.Add(res);

                var sizes = new List<VariantWriter.Size>();
                foreach (var size in scheme.Sizes)
                {
                    progress?.Invoke(scheme, $"{file} ({size.DisplayName})");
                    var sopt = Options(size.InternalName, Path.Combine(work, size.InternalName, file));
                    var sreport = Win7StyleImporter.Run(sopt);
                    ImportReportWriter.Write(sreport, Path.Combine(reportDir, $"{label} {XpThemeFiles.SafeName(size.InternalName)}"));
                    if (sreport.Status != ImportStatus.ReadyForVmTest)
                    {
                        report.Warnings.Add($"size {size.InternalName} not added: its port is blocked ({string.Join("; ", sreport.Errors.Take(3))}).");
                        continue;
                    }
                    sizes.Add(new VariantWriter.Size { Name = size.InternalName, DisplayName = size.DisplayName, PortPath = sopt.OutputPath });
                }

                if (res.Ok && o.Write)
                {
                    VariantWriter.Apply(opt.OutputPath, themeName, scheme.InternalName, scheme.DisplayName.Trim(), sizes, scheme.NormalSizeDisplayName);
                    report.Notes.Add($"variants: theme \"{themeName}\", color {scheme.InternalName} \"{scheme.DisplayName.Trim()}\", sizes NormalSize{string.Concat(sizes.Select(z => ", " + z.Name))}.");
                    CheckVariantsLoad(opt.OutputPath, scheme.InternalName, sizes, report);
                }
                res.ReportFiles = ImportReportWriter.Write(report, Path.Combine(reportDir, label));
                if (!res.Ok || !o.Write) continue;

                Directory.CreateDirectory(styleDir);
                res.StylePath = Path.Combine(styleDir, file);
                File.Copy(opt.OutputPath, res.StylePath, true);
                // the port's Shell\NormalColor (template shellstyle with this scheme's bitmaps) is this scheme's.
                string shellFrom = Path.Combine(work, "normal", "Shell", "NormalColor");
                if (Directory.Exists(shellFrom)) CopyDirectory(shellFrom, Path.Combine(styleDir, "Shell", scheme.InternalName));
                foreach (var lang in Directory.GetDirectories(Path.Combine(work, "normal")).Where(d => File.Exists(Path.Combine(d, file + ".mui"))))
                    CopyDirectory(lang, Path.Combine(styleDir, Path.GetFileName(lang)));

                res.ThemePath = Path.Combine(outDir, label + ".theme");
                IDictionary<string, System.Drawing.Color> colors;
                using (var written = new VisualStyle())
                {
                    written.Load(res.StylePath);
                    colors = XpThemeFiles.SysColors(written);
                }
                XpThemeFiles.WriteTheme(res.ThemePath, XpThemeFiles.BuildTheme(display, name, file, o.Wallpaper ?? XpThemeFiles.DefaultWallpaper, colors, scheme.InternalName));
            }
            if (o.Write) try { Directory.Delete(Path.Combine(outDir, ".work"), true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return results;
        }

        /// <summary>[Documentation] DisplayName of an xp style.</summary>
        public static string ReadThemeName(string path)
        {
            using (var style = new VisualStyle())
            {
                style.Load(path);
                return string.IsNullOrWhiteSpace(style.XpThemeDisplayName) ? null : style.XpThemeDisplayName.Trim();
            }
        }

        /// <summary>uxtheme itself loads every color/size pair the file now names; a failure blocks the scheme.</summary>
        private static void CheckVariantsLoad(string stylePath, string color, List<VariantWriter.Size> sizes, ImportReport report)
        {
            foreach (var size in new[] { "NormalSize" }.Concat(sizes.Select(s => s.Name)))
            {
                string err = UxthemeLoader.TryLoad(stylePath, color, size);
                if (err == null) report.Validation?.Checks.Add($"uxtheme loads {color}/{size}.");
                else
                {
                    report.Errors.Add($"variants: uxtheme can't load {color}/{size}: {err}");
                    report.Status = ImportStatus.Blocked;
                }
            }
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}
