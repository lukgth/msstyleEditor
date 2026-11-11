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
        /// <summary>[Control Panel\Desktop] Wallpaper; null (the default) reads the pack's own .theme desktop section
        /// and only falls back to <see cref="XpThemeFiles.DefaultWallpaper"/> when the pack ships no picture. The pack's
        /// metrics, icons, cursors, screensaver and sounds are read either way.</summary>
        public string Wallpaper { get; set; }
        public bool Write { get; set; }
        public List<string> PreferSource { get; set; } = new List<string>();
        public List<string> Exclude { get; set; } = new List<string>();
        public int? ImageTolerance { get; set; }
        /// <summary>force xp's top band when the source shellstyle has no pane markup (see <see cref="Win7ImportOptions.TaskBand"/>). Off by default.</summary>
        public bool TaskBand { get; set; } = false;
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
            // repeated --scheme values would port and write the same scheme twice.
            int requested = schemes.Count;
            schemes = schemes.GroupBy(s => s.InternalName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            bool duplicateSchemes = schemes.Count != requested;

            string outDir = Path.GetFullPath(o.OutDir);
            string name = XpThemeFiles.SafeName(o.Name ?? Path.GetFileNameWithoutExtension(o.SourcePath));
            // a dry run writes nothing into --out-dir, so its reports go to a folder of their own in %TEMP% (the paths
            // are printed like the real run's).
            string reportDir = o.Write
                ? Path.Combine(outDir, "reports")
                : Path.Combine(Path.GetTempPath(), "msstyle-xp-reports-" + Guid.NewGuid().ToString("N"));
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
                // the port always writes its candidate into the transient .work folder, dry run or not: the variant
                // rewrite and the uxtheme load check need a real file, and .work is deleted either way. The reports
                // are flipped back to "dry run" once the port is done.
                DryRun = false,
                OutputPath = output,
                SourceShellStylePath = o.ShellStylePath,
                PreferSource = o.PreferSource ?? new List<string>(),
                Exclude = o.Exclude ?? new List<string>(),
                ImageTolerance = o.ImageTolerance ?? 2,
                TaskBand = o.TaskBand,
            };
            string LabelOf(XpColorScheme scheme) => all.Count == 1 ? name : $"{name} {XpThemeFiles.SafeName(scheme.InternalName)}";
            // the transient per-scheme port folders; a dry run keeps them in %TEMP% so it writes nothing into --out-dir.
            string workRoot = o.Write
                ? Path.Combine(outDir, ".work")
                : Path.Combine(Path.GetTempPath(), "msstyle-xp-work-" + Guid.NewGuid().ToString("N"));
            string WorkOf(XpColorScheme scheme) => Path.Combine(workRoot, XpThemeFiles.SafeName(scheme.InternalName));

            // every scheme and size is ported in a folder of its own (.work\<scheme>\<size>), so the ports run in
            // parallel; the shared theme folder is assembled after, one scheme at a time in the source's order.
            var jobs = schemes.SelectMany(s => new[] { (Scheme: s, Size: (XpSize)null) }
                .Concat(s.Sizes.Select(z => (Scheme: s, Size: z)))).ToList();
            foreach (var s in schemes)
            {
                string work = WorkOf(s);
                TryDelete(work);
            }
            var ports = new (Win7ImportOptions Opt, ImportReport Report)[jobs.Count];
            var progressLock = new object();
            System.Threading.Tasks.Parallel.For(0, jobs.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                var (scheme, size) = jobs[i];
                string file = FileOf(scheme, all, name);
                var opt = Options(scheme, size?.InternalName, Path.Combine(WorkOf(scheme), size?.InternalName ?? "normal", file));
                // one scheme/size failing must not take the other jobs' reports with it: the failure is recorded in
                // that job's own report and the loop carries on.
                try
                {
                    lock (progressLock) progress?.Invoke(scheme, size == null ? file : $"{file} ({size.DisplayName})");
                    var report = Win7StyleImporter.Run(opt);
                    if (!o.Write)
                    {
                        // the port ran as a write (it had to, to produce a file the variant rewrite and the load
                        // check can read); the report must still describe a dry run.
                        report.DryRun = true;
                        report.Output = null;
                        // the importer's own blocked text says what failed; only the pass text claims a write.
                        if (report.Status == ImportStatus.ReadyForVmTest)
                            report.StatusText = "dry run passed offline validation. nothing was written; rerun with --write to get the candidate.";
                    }
                    ports[i] = (opt, report);
                }
                catch (Exception ex)
                {
                    var report = new ImportReport
                    {
                        Tool = "msstyleEditor xp importer",
                        DryRun = !o.Write,
                        Status = ImportStatus.Blocked,
                        StatusText = "blocked: the port threw before it could finish.",
                    };
                    report.Inputs["source"] = o.SourcePath + $" (scheme {scheme.InternalName}, size {size?.InternalName ?? "normal"})";
                    report.Inputs["template"] = o.TemplatePath;
                    report.Errors.Add("import aborted: " + ex.Message);
                    report.Validation.Blocked = true;
                    ports[i] = (opt, report);
                }
            });

            // a previous import's folder still holds schemes this run may no longer write (stale Shell\<scheme>,
            // .mui folders, extra .msstyles): the assembled folder must be exactly this run's output, otherwise an
            // uninstalled color scheme keeps looking installed. (The .work folder is separate and transient.)
            string cleanupNote = null, cleanupWarning = null;
            if (o.Write)
            {
                try
                {
                    if (Directory.Exists(styleDir)) DeleteOutputDir(styleDir, o.SourcePath, o.TemplatePath);
                    cleanupNote = $"output: cleaned previous {Path.GetFileName(styleDir)} folder";
                }
                catch (Exception ex) { cleanupWarning = "output: could not clean the previous style folder: " + ex.Message; }
            }

            var results = new List<XpSchemeResult>();
            // the jobs of one scheme are its normal-size port followed by its other sizes, in that order.
            int jobBase = 0;
            try
            {
                foreach (var scheme in schemes)
                {
                    int normalJob = jobBase;
                    jobBase += 1 + scheme.Sizes.Count;
                    string label = LabelOf(scheme);
                    var (opt, report) = ports[normalJob];
                    var res = new XpSchemeResult
                    {
                        Scheme = scheme,
                        Label = label,
                        Report = report,
                        StyleFolder = styleDir,
                    };
                    results.Add(res);
                    // run-level facts, stated once instead of once per scheme, and before this report is flushed.
                    if (results.Count == 1)
                    {
                        if (cleanupNote != null) report.Notes.Add(cleanupNote);
                        if (cleanupWarning != null) report.Warnings.Add(cleanupWarning);
                        if (duplicateSchemes) report.Warnings.Add("schemes: repeated --scheme values were reduced to one port per color scheme");
                    }
                    // one scheme's failure (a locked target, a resource rewrite uxtheme rejects) must not discard the
                    // schemes already assembled: it is recorded in this scheme's report and the loop carries on.
                    try
                    {
                        AssembleScheme(o, scheme, opt, report, res, all, themeName, name, styleDir, outDir, reportDir, workRoot, ports, normalJob);
                    }
                    catch (Exception ex)
                    {
                        report.Errors.Add("assembly aborted: " + ex.Message);
                        report.Status = ImportStatus.Blocked;
                        report.Validation.Blocked = true;
                        report.StatusText = "blocked: the theme folder could not be assembled, see the errors.";
                    }
                    finally
                    {
                        // written last: the assembly above adds the warnings that matter most (the .theme's size name,
                        // the wallpaper, the font check), and a report flushed earlier would drop them.
                        try { res.ReportFiles = ImportReportWriter.Write(report, Path.Combine(reportDir, label)); }
                        catch (Exception ex) { report.Errors.Add("report not written: " + ex.Message); }
                    }
                }
            }
            finally
            {
                // the transient port folders go whether or not the assembly got through every scheme.
                TryDelete(workRoot);
            }
            return results;
        }

        /// <summary>assembles one scheme's part of the theme folder: its size variants, the .theme and the style
        /// folder's Shell and language folders. Throws on any failure the caller records against the scheme.</summary>
        private static void AssembleScheme(XpThemeImportOptions o, XpColorScheme scheme, Win7ImportOptions opt, ImportReport report,
            XpSchemeResult res, List<XpColorScheme> all, string themeName, string name, string styleDir, string outDir, string reportDir,
            string workRoot, (Win7ImportOptions Opt, ImportReport Report)[] ports, int normalJob)
        {
            string label = res.Label;
            string file = FileOf(scheme, all, name);
            string display = all.Count == 1 ? themeName : $"{themeName} {scheme.DisplayName.Trim()}";
            string work = Path.Combine(workRoot, XpThemeFiles.SafeName(scheme.InternalName));
            // the folder views (the task pane and every view template) are the template's shellstyle: a lone
            // .msstyles template has no Shell folder, and the port would then open every folder window empty
            // (no band, no view templates) instead of looking like xp. The folder is named after the template's own
            // color variant, which is not always "NormalColor".
            string shellFrom = StyleCompanions.FindShellFolder(Path.Combine(work, "normal"));
            if (report.Status == ImportStatus.ReadyForVmTest && shellFrom == null)
            {
                report.Status = ImportStatus.Blocked;
                report.Validation.Blocked = true;
                report.Errors.Add("template: no Shell\\<color>\\shellstyle.dll next to the template .msstyles; the folder views need its shellstyle and UIFILEs, so nothing was written. Port from a full template folder (the .msstyles and its Shell folder), not from a lone .msstyles.");
            }
            // every size's own report is written even when this scheme is blocked: the size ports ran either way.
            var sizes = new List<VariantWriter.Size>();
            for (int i = 0; i < scheme.Sizes.Count; ++i)
            {
                var size = scheme.Sizes[i];
                var (sopt, sreport) = ports[normalJob + 1 + i];
                // the size's own port file is deleted with .work: the size lives inside the scheme's style as a variant.
                if (o.Write) sreport.Output = Path.Combine(styleDir, file);
                sreport.Notes.Add($"this size is a variant inside \"{file}\", not a separate style file.");
                ImportReportWriter.Write(sreport, Path.Combine(reportDir, $"{label} {XpThemeFiles.SafeName(size.InternalName)}"));
                if (!res.Ok) continue;
                if (sreport.Status != ImportStatus.ReadyForVmTest)
                {
                    report.Warnings.Add($"size {size.InternalName} not added: its port is blocked ({string.Join("; ", sreport.Errors.Take(3))}).");
                    continue;
                }
                sizes.Add(new VariantWriter.Size { Name = size.InternalName, DisplayName = size.DisplayName, PortPath = sopt.OutputPath });
            }
            if (!res.Ok) return;

            var variantChecks = VariantWriter.Apply(opt.OutputPath, themeName, scheme.InternalName, scheme.DisplayName.Trim(), sizes, scheme.NormalSizeDisplayName);
            foreach (var c in variantChecks) report.Validation?.Checks.Add(c);
            report.Notes.Add($"variants: theme \"{themeName}\", color {scheme.InternalName} \"{scheme.DisplayName.Trim()}\", sizes NormalSize{string.Concat(sizes.Select(z => ", " + z.Name))}.");
            // the load check runs on a dry run too: the port writes its candidate into .work either way, and a style
            // whose variant rewrite uxtheme rejects must not report ReadyForVmTest.
            CheckVariantsLoad(opt.OutputPath, scheme.InternalName, sizes, report);

            // the xp pack's .theme: desktop picture, metrics, icons, cursors, screensaver and sounds. The extras are
            // read regardless of --wallpaper; the picture itself is only taken from the pack when that option is
            // null. xp themes name paths that assumed the machine they were made on ("C:\WINDOWS\xanadu.jpg"), so
            // the file travels with the style and the generated .theme points at the copy.
            XpThemeFiles.Desktop desktop = XpThemeFiles.ReadDesktop(o.SourcePath, scheme.InternalName);
            string sourceDir = Path.GetDirectoryName(Path.GetFullPath(o.SourcePath));
            var extras = XpThemeExtras.Read(desktop, sourceDir, styleDir, name, o.Write);
            report.Notes.AddRange(extras.Notes);
            report.Warnings.AddRange(extras.Warnings);

            string wallpaper;
            if (o.Wallpaper != null)
            {
                wallpaper = o.Wallpaper;
                report.Notes.Add($"desktop: wallpaper from --wallpaper ({o.Wallpaper}); the pack's {(desktop?.Wallpaper != null ? "\"" + desktop.Wallpaper + "\"" : "desktop section")} is not used for the picture");
            }
            else if (desktop?.WallpaperPath != null && File.Exists(desktop.WallpaperPath))
            {
                string wallpaperFile = XpThemeFiles.SafeName(Path.GetFileName(desktop.WallpaperPath));
                if (o.Write)
                {
                    Directory.CreateDirectory(styleDir);
                    File.Copy(desktop.WallpaperPath, Path.Combine(styleDir, wallpaperFile), true);
                }
                // %WinDir% expands wherever the value ends up (Control Panel\Desktop\Wallpaper too);
                // %ResourceDir% is only resolved inside [VisualStyles] Path.
                wallpaper = $@"%WinDir%\Resources\Themes\{name}\{wallpaperFile}";
                report.Notes.Add($"desktop: wallpaper \"{Path.GetFileName(desktop.WallpaperPath)}\" {(o.Write ? "copied" : "would be copied")} from {Path.GetFileName(desktop.ThemePath)}; xp showed it with TileWallpaper={(desktop.TileWallpaper ? 1 : 0)} WallpaperStyle={desktop.WallpaperStyle}");
            }
            else
            {
                wallpaper = XpThemeFiles.DefaultWallpaper;
                if (desktop?.Wallpaper is string missing)
                    report.Warnings.Add($"desktop: {Path.GetFileName(desktop.ThemePath)} names wallpaper \"{missing}\" but that file isn't next to the style; using {XpThemeFiles.DefaultWallpaper}");
                else
                    report.Notes.Add($"desktop: no picture in the pack's .theme files; using the Windows 10 default {XpThemeFiles.DefaultWallpaper}");
            }

            // which fonts the result asks for, and whether this machine has them (nothing is installed for a user
            // who didn't ask: a missing face silently falls back to whatever GDI picks, so it is worth a warning).
            ReportFonts(report, new[] { File.Exists(opt.OutputPath) ? opt.OutputPath : null, o.SourcePath }, extras);

            if (!o.Write) return;

            Directory.CreateDirectory(styleDir);
            res.StylePath = Path.Combine(styleDir, file);
            File.Copy(opt.OutputPath, res.StylePath, true);
            // the port's Shell\<color> (template shellstyle with this scheme's bitmaps) is this scheme's.
            CopyDirectory(shellFrom, Path.Combine(styleDir, "Shell", scheme.InternalName));
            foreach (var lang in Directory.GetDirectories(Path.Combine(work, "normal")).Where(d => File.Exists(Path.Combine(d, file + ".mui"))))
                CopyDirectory(lang, Path.Combine(styleDir, Path.GetFileName(lang)));

            res.ThemePath = Path.Combine(outDir, label + ".theme");
            IDictionary<string, System.Drawing.Color> colors;
            using (var written = new VisualStyle())
            {
                written.Load(res.StylePath);
                colors = XpThemeFiles.SysColors(written);
            }
            // [VisualStyles] Size must be the size variant the written style actually names (VMAP's size column),
            // otherwise uxtheme finds no variant for the pair and the .theme loads the wrong size.
            string normalSize = NormalSizeName(res.StylePath, out string sizeError);
            if (sizeError != null) report.Warnings.Add("theme: " + sizeError);
            XpThemeFiles.WriteTheme(res.ThemePath, XpThemeFiles.BuildTheme(display, name, file, wallpaper, colors, scheme.InternalName, desktop, extras, normalSize));
            // the report names the file the run kept, not the .work copy it is deleted with.
            report.Output = res.StylePath;
        }

        /// <summary>the file name of a scheme's .msstyles: the theme's name for a single-scheme style, else the scheme's.</summary>
        private static string FileOf(XpColorScheme scheme, List<XpColorScheme> all, string name)
            => (all.Count == 1 ? name : XpThemeFiles.SafeName(scheme.InternalName)) + ".msstyles";

        /// <summary>the size name the written style's VMAP carries for its normal variant; [VisualStyles] Size has to
        /// match it or uxtheme finds no variant for the requested color/size pair.</summary>
        private static string NormalSizeName(string stylePath, out string error)
        {
            error = null;
            try
            {
                using (var inv = new ResourceInventory(stylePath))
                {
                    byte[] Get(string type) => inv.Entries.Where(e => e.Type == type && e.Name == type).Select(inv.GetData).FirstOrDefault();
                    byte[] cmap = Get("CMAP"), bcmap = Get("BCMAP"), vmap = Get("VMAP"), rmap = Get("RMAP");
                    if (cmap == null || vmap == null || rmap == null)
                    {
                        error = "the style has no CMAP/VMAP/RMAP; [VisualStyles] Size stays NormalSize";
                        return "NormalSize";
                    }
                    var maps = VariantMaps.Read(cmap, bcmap, vmap, rmap);
                    if (maps.Variants.Count == 0)
                    {
                        error = "the style's VMAP names no variant; [VisualStyles] Size stays NormalSize";
                        return "NormalSize";
                    }
                    return maps.Variants[0].Size;
                }
            }
            catch (Exception ex)
            {
                error = "can't read the style's variants (" + ex.Message + "); [VisualStyles] Size stays NormalSize";
                return "NormalSize";
            }
        }

        /// <summary>the faces the port asks for (every FONT record of the styles plus the .theme's metric blobs)
        /// checked against the installed fonts. Nothing is installed (no AddFontResource): a missing face silently falls
        /// back to a substitute, so it is reported with the substitution risk it carries.</summary>
        private static void ReportFonts(ImportReport report, IEnumerable<string> stylePaths, XpThemeExtras extras)
        {
            if (report == null) return;
            var faces = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void AddFace(string face, string where)
            {
                face = face?.Trim();
                if (string.IsNullOrEmpty(face)) return;
                if (!faces.TryGetValue(face, out var list)) faces[face] = list = new List<string>();
                if (!list.Contains(where)) list.Add(where);
            }
            foreach (string path in stylePaths.Where(p => p != null && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using (var style = new VisualStyle())
                    {
                        style.Load(path);
                        foreach (var prop in style.Classes.Values.SelectMany(c => c.Parts.Values).SelectMany(p => p.States.Values).SelectMany(s => s.Properties))
                        {
                            if (prop.Header.typeID != (int)IDENTIFIER.FONT) continue;
                            if (!style.PreferredStringTable.TryGetValue(prop.Header.shortFlag, out string text)) continue;
                            var spec = FontSpec.Parse(text);
                            if (spec.Error != null)
                            {
                                report.Warnings.Add($"fonts: {Path.GetFileName(path)} has a font string uxtheme rejects (\"{text}\": {spec.Error})");
                                continue;
                            }
                            AddFace(spec.Face, Path.GetFileName(path));
                        }
                    }
                }
                catch (Exception ex) { report.Warnings.Add($"fonts: can't read \"{Path.GetFileName(path)}\" to check its faces: {ex.Message}"); }
            }
            foreach (var face in FacesInMetrics(extras)) AddFace(face, "metrics");
            if (faces.Count == 0) return;
            HashSet<string> installed;
            try { installed = new HashSet<string>(new System.Drawing.Text.InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase); }
            catch (Exception ex) { report.Warnings.Add("fonts: can't enumerate installed fonts: " + ex.Message); return; }
            var missing = faces.Keys.Where(f => !installed.Contains(f)).OrderBy(f => f).ToList();
            if (missing.Count == 0)
                report.Notes.Add($"fonts: {faces.Count} faces referenced ({string.Join(", ", faces.Keys.OrderBy(f => f))}); all installed, and none are installed by this import");
            else
                report.Warnings.Add($"fonts: {missing.Count} of {faces.Count} referenced faces aren't installed ({string.Join(", ", missing)}); Windows substitutes a fallback face, so text metrics and the theme's look can differ. The import never installs fonts; add the pack's own faces yourself if you want them.");
        }

        /// <summary>the face names inside the .theme's [Metrics] blobs, decoded by the one
        /// <see cref="XpAnsiMetrics"/> layout the applier uses too.</summary>
        private static IEnumerable<string> FacesInMetrics(XpThemeExtras extras)
        {
            var faces = new List<string>();
            if (extras == null) return faces;
            foreach (var kv in extras.Metrics)
            {
                byte[] b = ParseBytes(kv.Value);
                int[] offsets = XpAnsiMetrics.FontOffsets(kv.Key, b);
                if (offsets == null) continue;
                foreach (int at in offsets)
                {
                    string face = XpAnsiMetrics.FaceName(b, at);
                    if (face.Length != 0) faces.Add(face);
                }
            }
            return faces;
        }

        /// <summary>the .theme's byte lists are decimal numbers separated by spaces (Windows Classic.theme).</summary>
        private static byte[] ParseBytes(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var b = new byte[parts.Length];
            for (int i = 0; i < parts.Length; ++i) if (!byte.TryParse(parts[i], out b[i])) return null;
            return b;
        }

        /// <summary>removes an output folder before it is re-assembled, never one that holds the source or template.</summary>
        private static void DeleteOutputDir(string dir, string sourcePath, string templatePath)
        {
            string full = Path.GetFullPath(dir);
            foreach (var p in new[] { sourcePath, templatePath }.Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.GetDirectoryName(Path.GetFullPath(p))))
            {
                if (p == null) continue;
                if (full.Equals(p, StringComparison.OrdinalIgnoreCase) || p.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"refusing to delete \"{full}\": it holds the source or template .msstyles");
            }
            Directory.Delete(full, true);
        }

        /// <summary>deletes a transient folder; a file still mapped by uxtheme is not a reason to fail the import
        /// (the caller already wrote everything it reports).</summary>
        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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

        /// <summary>uxtheme itself loads every color/size pair the file now names; a failure blocks the scheme, and a
        /// check that never ran is reported as skipped instead of as a pass.</summary>
        private static void CheckVariantsLoad(string stylePath, string color, List<VariantWriter.Size> sizes, ImportReport report)
        {
            string normal = NormalSizeName(stylePath, out _);
            foreach (var size in new[] { normal }.Concat(sizes.Select(s => s.Name)))
            {
                var res = UxthemeLoader.TryLoad(stylePath, color, size, out string err);
                if (res == UxthemeLoadResult.Loaded)
                {
                    report.Validation?.Checks.Add($"uxtheme loads {color}/{size}.");
                }
                else if (res == UxthemeLoadResult.Skipped)
                {
                    report.Warnings.Add($"variants: uxtheme load check skipped for {color}/{size}: {err}.");
                }
                else
                {
                    report.Errors.Add($"variants: uxtheme can't load {color}/{size}: {err}");
                    report.Status = ImportStatus.Blocked;
                    report.Validation.Blocked = true;
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
