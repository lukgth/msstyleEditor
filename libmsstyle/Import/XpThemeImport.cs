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

        /// <param name="progress">called as each scheme/size port starts; the ports run in parallel, so calls come from
        /// worker threads (one at a time) and in no fixed order. May be null.</param>
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
            Win7ImportOptions Options(XpColorScheme scheme, string size, string output) => new Win7ImportOptions
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
            string FileOf(XpColorScheme scheme) => (all.Count == 1 ? name : XpThemeFiles.SafeName(scheme.InternalName)) + ".msstyles";
            string LabelOf(XpColorScheme scheme) => all.Count == 1 ? name : $"{name} {XpThemeFiles.SafeName(scheme.InternalName)}";
            string WorkOf(XpColorScheme scheme) => Path.Combine(outDir, ".work", XpThemeFiles.SafeName(scheme.InternalName));

            // every scheme and size is ported in a folder of its own (.work\<scheme>\<size>), so the ports run in
            // parallel; the shared theme folder is assembled after, one scheme at a time in the source's order.
            var jobs = schemes.SelectMany(s => new[] { (Scheme: s, Size: (XpSize)null) }
                .Concat(s.Sizes.Select(z => (Scheme: s, Size: z)))).ToList();
            foreach (var s in schemes)
            {
                string work = WorkOf(s);
                if (Directory.Exists(work)) Directory.Delete(work, true);
            }
            var ports = new (Win7ImportOptions Opt, ImportReport Report)[jobs.Count];
            var progressLock = new object();
            System.Threading.Tasks.Parallel.For(0, jobs.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                var (scheme, size) = jobs[i];
                string file = FileOf(scheme);
                lock (progressLock) progress?.Invoke(scheme, size == null ? file : $"{file} ({size.DisplayName})");
                var opt = Options(scheme, size?.InternalName, Path.Combine(WorkOf(scheme), size?.InternalName ?? "normal", file));
                ports[i] = (opt, Win7StyleImporter.Run(opt));
            });

            var results = new List<XpSchemeResult>();
            int at = 0;
            foreach (var scheme in schemes)
            {
                string label = LabelOf(scheme);
                string file = FileOf(scheme);
                string display = all.Count == 1 ? themeName : $"{themeName} {scheme.DisplayName.Trim()}";
                string work = WorkOf(scheme);
                var (opt, report) = ports[at++];
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
                    var (sopt, sreport) = ports[at++];
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

                // the xp pack's own desktop picture, if it shipped one: xp themes point at a path that assumed the
                // machine they were made on ("C:\WINDOWS\xanadu.jpg"), so the file travels with the style and the
                // generated .theme points at the copy. Resolved before the report is written, so the note lands in it.
                XpThemeFiles.Desktop desktop = null;
                XpThemeExtras extras = null;
                string wallpaper = o.Wallpaper;
                if (wallpaper == null)
                {
                    desktop = XpThemeFiles.ReadDesktop(o.SourcePath, scheme.InternalName);
                    if (desktop != null && res.Ok && o.Write)
                    {
                        if (desktop.WallpaperPath != null && File.Exists(desktop.WallpaperPath))
                        {
                            Directory.CreateDirectory(styleDir);
                            string wallpaperFile = XpThemeFiles.SafeName(Path.GetFileName(desktop.WallpaperPath));
                            File.Copy(desktop.WallpaperPath, Path.Combine(styleDir, wallpaperFile), true);
                            // %WinDir% expands wherever the value ends up (Control Panel\Desktop\Wallpaper too);
                            // %ResourceDir% is only resolved inside [VisualStyles] Path.
                            wallpaper = $@"%WinDir%\Resources\Themes\{name}\{wallpaperFile}";
                            report.Notes.Add($"desktop: wallpaper \"{Path.GetFileName(desktop.WallpaperPath)}\" copied from {Path.GetFileName(desktop.ThemePath)}; xp showed it with TileWallpaper={(desktop.TileWallpaper ? 1 : 0)} WallpaperStyle={desktop.WallpaperStyle}");
                        }
                        else if (desktop.Wallpaper != null)
                        {
                            report.Warnings.Add($"desktop: {Path.GetFileName(desktop.ThemePath)} names wallpaper \"{desktop.Wallpaper}\" but that file isn't next to the style; using {XpThemeFiles.DefaultWallpaper}");
                        }
                        // metrics, cursors, icons, screensaver, sounds: only what the pack shipped or this machine has.
                        extras = XpThemeExtras.Read(desktop, Path.GetDirectoryName(Path.GetFullPath(o.SourcePath)), styleDir, name);
                        report.Notes.AddRange(extras.Notes);
                        report.Warnings.AddRange(extras.Warnings);
                    }
                }
                wallpaper = wallpaper ?? XpThemeFiles.DefaultWallpaper;
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
                XpThemeFiles.WriteTheme(res.ThemePath, XpThemeFiles.BuildTheme(display, name, file, wallpaper, colors, scheme.InternalName, desktop, extras));
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
