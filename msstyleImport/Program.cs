using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using libmsstyle;
using libmsstyle.Import;

namespace msstyleImport
{
    class Program
    {
        // exit codes: 0 ready for vm test / ok, 1 blocked, 2 usage, 3 crashed
        static int Main(string[] args)
        {
            if (args.Length == 0) return Usage();
            try
            {
                switch (args[0])
                {
                    case "import": return Import(args.Skip(1).ToArray());
                    case "import-xp": return ImportXp(args.Skip(1).ToArray());
                    case "schemes": return args.Length == 2 ? Schemes(args[1]) : Usage();
                    case "noop": return NoOp(args.Skip(1).ToArray());
                    case "inventory": return args.Length == 3 ? Inventory(args[1], args[2]) : Usage();
                    case "apply-theme":
                        {
                            int code = ApplyTheme(args.Skip(1).ToArray(), out bool badUsage);
                            return badUsage ? Usage() : code;
                        }
                    case "dumpres": return args.Length == 4 ? DumpResource(args[1], args[2], args[3]) : Usage();
                    case "reslist": return args.Length == 2 ? ListResources(args[1]) : Usage();
                    default: return Usage();
                }
            }
            catch (UsageError ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return Usage();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 3;
            }
        }

        static int Usage()
        {
            Console.Error.WriteLine(
@"usage:
  msstyleImport import --source <custom win7.msstyles> --template <working win10.msstyles>
                       --report <path prefix> [--base <stock win7 aero.msstyles, default: built in>]
                       [--companion <style whose Shell\.mui folders travel with the candidate>]
                       [--write --out <candidate.msstyles>]
                       [--prefer-source Class|Part|Prop]... [--exclude Class|Part|Prop]...
                       [--image-tolerance N]
      dry run unless --write is given. reports: <prefix>.json, <prefix>.md, <prefix>.threeway.json
  msstyleImport import-xp --source <custom xp.msstyles> --template <working win10 xp port.msstyles>
                       --out-dir <dir> [--scheme <name>]... [--base <stock xp luna.msstyles, default: built in>]
                       [--companion <style whose Shell and .mui folders travel with the candidate>]
                       [--base-scheme NormalColor|Metallic|HomeStead] [--name <display name>]
                       [--shellstyle <xp shellstyle.dll, default: Shell\<scheme>\shellstyle.dll next to the source>]
                       [--wallpaper <path>] [--write]
                       [--task-band] [--prefer-source Class|Part|Prop]... [--exclude Class|Part|Prop]...
                       [--image-tolerance N]
      the task pane placement follows the xp shellstyle's own markup (top/bottom band or left/right sidebar);
      --task-band forces xp's full-width top band for sources whose markup has no placement.
      the wallpaper is the one the xp pack's .theme files name next to the source (copied into the style folder);
      --wallpaper overrides it, and %WinDir%\Web\Wallpaper\Bliss.bmp is used when the pack ships none.
      one output per color scheme (all schemes unless --scheme is given):
        <out-dir>\<Label>\<Label>.msstyles + Shell\ (the template's, with the source shellstyle's bitmaps),
        <out-dir>\<Label>.theme, reports in <out-dir>\reports\. <Label> is <Name>, or <Name> <scheme> when the
        style has several schemes. Copy <out-dir>\<Label> to %WINDIR%\Resources\Themes and open the .theme.
      dry run (reports only) unless --write is given: nothing is written into <out-dir>, the reports go to a
      temporary folder and their paths are printed.
  msstyleImport schemes <xp.msstyles>
      lists the color schemes of an xp style.
  msstyleImport noop --template <style> --out <copy.msstyles>
      saves the template through libmsstyle without changes and validates the copy against it.
  msstyleImport reslist <pe>
      every resource of a PE as type/name/language/size lines (debug helper).
  msstyleImport dumpres <pe> <type> <name>
      raw bytes of one PE resource (integer ids as #12), as a size line and a base64 line (debug helper).
  msstyleImport inventory <style> <out.tsv>
      flat dump: classes, resources, string tables, every property by name.
  msstyleImport apply-theme <installed .theme> [--style-only | --appearance-only]
      applies an installed theme without any UI: the visual style through uxtheme's SetSystemVisualStyle, then the
      .theme's colors, desktop picture, metrics, cursors, sounds and default icons. --style-only stops after the visual
      style (fast; for iterating on a port, existing windows keep their colors until the next full apply). prints one line per section.
      --appearance-only applies explicit default icons and Light/Dark app/system modes only; all other shell settings stay unchanged.
      exit 0 when the style was accepted, 1 when it was rejected (the lines say why).
      this does write the machine's current theme/colors: that is the point, and it's what deskn.cpl does.");
            return 2;
        }

        /// <summary>apply-theme takes exactly one .theme and at most one selective apply option; anything else is a usage error
        /// (return true so Main reports exit 2 instead of applying something the caller didn't ask for).</summary>
        static int ApplyTheme(string[] args, out bool usageError)
        {
            usageError = false;
            string themePath = null;
            bool styleOnly = false, appearanceOnly = false;
            foreach (var a in args)
            {
                if (a == "--style-only")
                {
                    if (styleOnly || appearanceOnly) { usageError = true; return 2; }
                    styleOnly = true;
                }
                else if (a == "--appearance-only")
                {
                    if (styleOnly || appearanceOnly) { usageError = true; return 2; }
                    appearanceOnly = true;
                }
                else if (a.StartsWith("--") || themePath != null) { usageError = true; return 2; }
                else themePath = a;
            }
            if (themePath == null) { usageError = true; return 2; }
            var r = appearanceOnly ? XpThemeApply.ApplyAppearanceExtras(themePath) : XpThemeApply.Apply(themePath, styleOnly);
            foreach (var n in r.Notes) Console.WriteLine("ok   " + n);
            // once on stdout: printing warnings to stderr as well made every warning look like two.
            foreach (var w in r.Warnings) Console.WriteLine("warn " + w);
            return r.Ok ? 0 : 1;
        }

        // every resource type/name/language of a PE (one per line), debug helper.
        static int ListResources(string pe)
        {
            using (var inv = new libmsstyle.Import.ResourceInventory(pe))
                foreach (var e in inv.Entries)
                    Console.WriteLine($"{e.Type}\t{e.Name}\t{e.Language}\t{e.Size}");
            return 0;
        }

        // raw bytes of one PE resource: dumpres <pe> <type> <name>, both "#500" for integer ids. debug helper.
        static int DumpResource(string pe, string type, string name)
        {
            using (var inv = new libmsstyle.Import.ResourceInventory(pe))
            {
                var e = inv.Entries.FirstOrDefault(x => x.Type == type && x.Name == name);
                if (e == null) { Console.Error.WriteLine("no resource " + type + "/" + name); return 1; }
                byte[] b = inv.GetData(e);
                Console.Out.Write(b.Length + "\n");
                Console.Out.Write(Convert.ToBase64String(b));
                Console.Out.Write("\n");
            }
            return 0;
        }

        /// <summary>usage errors (unknown flag, missing value, flag given twice) exit 2; only real failures exit 3.</summary>
        private sealed class UsageError : Exception
        {
            public UsageError(string message) : base(message) { }
        }

        /// <summary>Parses --options; only the names a command declares are accepted, so a mistyped or foreign flag
        /// fails loudly instead of being silently ignored.</summary>
        static Dictionary<string, List<string>> ParseArgs(string[] args, string[] flags, string[] repeated, string[] values)
        {
            var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; ++i)
            {
                if (!args[i].StartsWith("--") || args[i].Length == 2) throw new UsageError($"unexpected argument \"{args[i]}\"");
                string k = args[i].Substring(2);
                bool isFlag = flags.Contains(k, StringComparer.OrdinalIgnoreCase);
                bool isValue = values.Contains(k, StringComparer.OrdinalIgnoreCase)
                    || repeated.Contains(k, StringComparer.OrdinalIgnoreCase);
                if (!isFlag && !isValue) throw new UsageError($"unknown option --{k}");
                if (!d.TryGetValue(k, out var l)) d[k] = l = new List<string>();
                else if (l.Count > 0 && !repeated.Contains(k, StringComparer.OrdinalIgnoreCase)) throw new UsageError($"--{k} is given twice");
                if (isFlag) continue;
                if (i + 1 >= args.Length) throw new UsageError($"--{k} needs a value");
                l.Add(args[++i]);
            }
            return d;
        }

        static string One(Dictionary<string, List<string>> a, string k, bool required = true)
        {
            if (a.TryGetValue(k, out var l) && l.Count > 0) return l[l.Count - 1];
            if (required) throw new ArgumentException($"--{k} is required");
            return null;
        }

        static int Import(string[] args)
        {
            var a = ParseArgs(args, new[] { "write", "task-band" }, new[] { "prefer-source", "exclude" },
                new[] { "source", "template", "base", "companion", "out", "report", "image-tolerance" });
            var opt = new Win7ImportOptions
            {
                SourcePath = One(a, "source"),
                BasePath = One(a, "base", required: false),
                TemplatePath = One(a, "template"),
                CompanionSourcePath = One(a, "companion", required: false),
                DryRun = !a.ContainsKey("write"),
                TaskBand = a.ContainsKey("task-band"),
                PreferSource = a.TryGetValue("prefer-source", out var ps) ? ps : new List<string>(),
                Exclude = a.TryGetValue("exclude", out var ex) ? ex : new List<string>(),
            };
            if (!opt.DryRun) opt.OutputPath = One(a, "out");
            if (a.ContainsKey("image-tolerance"))
            {
                if (!int.TryParse(One(a, "image-tolerance"), out int tolerance)) throw new UsageError("--image-tolerance needs a number");
                opt.ImageTolerance = tolerance;
            }
            string prefix = One(a, "report");

            var report = Win7StyleImporter.Run(opt);
            var files = ImportReportWriter.Write(report, prefix);
            PrintReport(report, files);
            return report.Status == ImportStatus.ReadyForVmTest ? 0 : 1;
        }

        static void PrintReport(ImportReport report, IEnumerable<string> files)
        {
            if (report == null) { Console.WriteLine("status: no report (the import stopped before it could be built)"); return; }
            Console.WriteLine($"status: {report.Status}");
            Console.WriteLine(report.StatusText ?? "");
            foreach (var kv in report.Counts.OrderBy(k => k.Key)) Console.WriteLine($"  {kv.Key}: {kv.Value}");
            // the "validation: " copies are printed under the validation summary; counting them here printed every
            // validation problem twice.
            foreach (var e in report.ErrorsWithoutValidation) Console.WriteLine("error: " + e);
            var validation = report.Validation;
            Console.WriteLine($"{report.WarningsWithoutValidation.Count} warnings, {validation?.Checks.Count ?? 0} validation checks passed, {validation?.Errors.Count ?? 0} failed");
            if (report.Output != null) Console.WriteLine("candidate: " + report.Output);
            if (files == null) return;
            foreach (var f in files.Where(f => f != null)) Console.WriteLine("report: " + f);
        }

        static int Schemes(string path)
        {
            foreach (var s in XpThemeImport.ReadSchemes(path)) Console.WriteLine($"{s.InternalName}\t{s.DisplayName}\t{s.IniResourceName}");
            return 0;
        }

        /// <summary>
        /// Ports every chosen color scheme of an xp style onto a win10 xp port, one style + .theme
        /// per scheme. Returns 0 only when every scheme passed.
        /// </summary>
        static int ImportXp(string[] args)
        {
            var a = ParseArgs(args, new[] { "write", "task-band" }, new[] { "scheme", "prefer-source", "exclude" },
                new[] { "source", "template", "companion", "out-dir", "name", "base", "base-scheme", "shellstyle", "wallpaper", "image-tolerance" });
            var o = new XpThemeImportOptions
            {
                SourcePath = One(a, "source"),
                TemplatePath = One(a, "template"),
                CompanionSourcePath = One(a, "companion", required: false),
                OutDir = One(a, "out-dir"),
                Schemes = a.TryGetValue("scheme", out var ws) ? ws : null,
                Name = One(a, "name", required: false),
                BasePath = One(a, "base", required: false),
                BaseScheme = One(a, "base-scheme", required: false) ?? "NormalColor",
                ShellStylePath = One(a, "shellstyle", required: false),
                Wallpaper = One(a, "wallpaper", required: false),
                Write = a.ContainsKey("write"),
                TaskBand = a.ContainsKey("task-band"),
                PreferSource = a.TryGetValue("prefer-source", out var ps) ? ps : new List<string>(),
                Exclude = a.TryGetValue("exclude", out var ex) ? ex : new List<string>(),
            };
            if (a.ContainsKey("image-tolerance"))
            {
                if (!int.TryParse(One(a, "image-tolerance"), out int tolerance)) throw new UsageError("--image-tolerance needs a number");
                o.ImageTolerance = tolerance;
            }

            var results = XpThemeImport.Run(o, (scheme, file) => Console.WriteLine($"== {scheme} -> {file}"));
            foreach (var r in results)
            {
                PrintReport(r.Report, r.ReportFiles);
                if (r.ThemePath != null) Console.WriteLine("theme: " + r.ThemePath);
            }
            var written = results.Where(x => x.ThemePath != null).ToList();
            if (written.Count > 0)
                Console.WriteLine($"install: copy \"{written[0].StyleFolder}\" to %WINDIR%\\Resources\\Themes\\{Path.GetFileName(written[0].StyleFolder)} and the .theme files next to it, then open one of: "
                    + string.Join(", ", written.Select(r => "\"" + r.ThemePath + "\"")));
            // a scheme whose port or assembly threw is recorded in its report instead of ending the run, so the
            // crash has to be read back out of the reports to keep exit 3 meaning "crashed".
            if (results.Any(r => r.Report?.Errors.Any(e => e.StartsWith("import aborted:") || e.StartsWith("assembly aborted:")) == true)) return 3;
            return results.All(r => r.Ok) ? 0 : 1;
        }

        static int NoOp(string[] args)
        {
            var a = ParseArgs(args, new string[0], new string[0], new[] { "template", "out" });
            string template = One(a, "template"), output = One(a, "out");
            using (var style = new VisualStyle())
            {
                style.Load(template);
                style.Save(output);
            }
            var v = StyleValidator.ValidateUnchanged(template, output);
            foreach (var c in v.Checks) Console.WriteLine("ok: " + c);
            foreach (var w in v.Warnings) Console.WriteLine("warn: " + w);
            foreach (var e in v.Errors) Console.WriteLine("FAIL: " + e);
            return v.Passed ? 0 : 1;
        }

        static int Inventory(string stylePath, string outPath)
        {
            using (var style = new VisualStyle())
            using (var inv = new ResourceInventory(stylePath))
            {
                style.Load(stylePath);
                var model = new SemanticModel(style);
                var sb = new StringBuilder();
                sb.AppendLine($"#platform\t{style.Platform}\tmachine\t{inv.Machine}\tclasses\t{style.Classes.Count}\tprops\t{style.NumProperties}");
                foreach (var d in style.LoadDiagnostics) sb.AppendLine("#diag\t" + d);
                foreach (var c in style.Classes.Values.OrderBy(c => c.ClassId))
                    sb.AppendLine($"#class\t{c.ClassId}\t{c.ClassName}\tbase\t{c.BaseClassId}");
                foreach (var r in inv.Entries)
                    sb.AppendLine($"#res\t{r.Type}\t{r.Name}\t{r.Language}\t{r.Size}\t{r.Sha1}");
                foreach (var t in style.StringTables)
                    foreach (var s in t.Value.OrderBy(s => s.Key))
                        sb.AppendLine($"#str\t{t.Key}\t{s.Key}\t{s.Value.Replace("\t", " ").Replace("\n", " ")}");
                foreach (var e in model.Entries)
                {
                    var info = e.ResourceType == null ? null : model.GetImageInfo(e.ResourceType, e.ResourceId);
                    sb.AppendLine($"{e.ClassName}\t{e.PartId}\t{e.PartName}\t{e.StateId}\t{e.NameId}\t{e.PropertyName}\t{e.TypeId}\t{e.RawValue}\t{e.Value}\t" +
                        (e.ResourceType == null ? "" : info == null ? "missing" : $"{info.Width}x{info.Height}"));
                }
                File.WriteAllText(outPath, sb.ToString());
            }
            return 0;
        }
    }
}
