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
                    default: return Usage();
                }
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
                       [--write --out <candidate.msstyles>]
                       [--prefer-source Class|Part|Prop]... [--exclude Class|Part|Prop]...
                       [--image-tolerance N]
      dry run unless --write is given. reports: <prefix>.json, <prefix>.md, <prefix>.threeway.json
  msstyleImport import-xp --source <custom xp.msstyles> --template <working win10 xp port.msstyles>
                       --out-dir <dir> [--scheme <name>]... [--base <stock xp luna.msstyles, default: built in>]
                       [--base-scheme NormalColor|Metallic|HomeStead] [--name <display name>]
                       [--shellstyle <xp shellstyle.dll, default: Shell\<scheme>\shellstyle.dll next to the source>]
                       [--wallpaper <path>] [--write]
                       [--prefer-source Class|Part|Prop]... [--exclude Class|Part|Prop]... [--image-tolerance N]
      one output per color scheme (all schemes unless --scheme is given):
        <out-dir>\<Label>\<Label>.msstyles + Shell\ (the template's, with the source shellstyle's bitmaps),
        <out-dir>\<Label>.theme, reports in <out-dir>\reports\. <Label> is <Name>, or <Name> <scheme> when the
        style has several schemes. Copy <out-dir>\<Label> to %WINDIR%\Resources\Themes and open the .theme.
      dry run (reports only) unless --write is given.
  msstyleImport schemes <xp.msstyles>
      lists the color schemes of an xp style.
  msstyleImport noop --template <style> --out <copy.msstyles>
      saves the template through libmsstyle without changes and validates the copy against it.
  msstyleImport inventory <style> <out.tsv>
      flat dump: classes, resources, string tables, every property by name.

nothing here applies or installs a theme.");
            return 2;
        }

        static Dictionary<string, List<string>> ParseArgs(string[] args)
        {
            var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; ++i)
            {
                if (!args[i].StartsWith("--")) throw new ArgumentException($"unexpected argument \"{args[i]}\"");
                string k = args[i].Substring(2);
                if (!d.TryGetValue(k, out var l)) d[k] = l = new List<string>();
                if (k == "write") continue;
                if (i + 1 >= args.Length) throw new ArgumentException($"--{k} needs a value");
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
            var a = ParseArgs(args);
            var opt = new Win7ImportOptions
            {
                SourcePath = One(a, "source"),
                BasePath = One(a, "base", required: false),
                TemplatePath = One(a, "template"),
                DryRun = !a.ContainsKey("write"),
                PreferSource = a.TryGetValue("prefer-source", out var ps) ? ps : new List<string>(),
                Exclude = a.TryGetValue("exclude", out var ex) ? ex : new List<string>(),
            };
            if (!opt.DryRun) opt.OutputPath = One(a, "out");
            if (a.ContainsKey("image-tolerance")) opt.ImageTolerance = int.Parse(One(a, "image-tolerance"));
            string prefix = One(a, "report");

            var report = Win7StyleImporter.Run(opt);
            var files = ImportReportWriter.Write(report, prefix);
            PrintReport(report, files);
            return report.Status == ImportStatus.ReadyForVmTest ? 0 : 1;
        }

        static void PrintReport(ImportReport report, IEnumerable<string> files)
        {
            Console.WriteLine($"status: {report.Status}");
            Console.WriteLine(report.StatusText);
            foreach (var kv in report.Counts.OrderBy(k => k.Key)) Console.WriteLine($"  {kv.Key}: {kv.Value}");
            foreach (var e in report.Errors) Console.WriteLine("error: " + e);
            Console.WriteLine($"{report.Warnings.Count} warnings, {report.Validation.Checks.Count} validation checks passed, {report.Validation.Errors.Count} failed");
            if (report.Output != null) Console.WriteLine("candidate: " + report.Output);
            foreach (var f in files) Console.WriteLine("report: " + f);
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
            var a = ParseArgs(args);
            var o = new XpThemeImportOptions
            {
                SourcePath = One(a, "source"),
                TemplatePath = One(a, "template"),
                OutDir = One(a, "out-dir"),
                Schemes = a.TryGetValue("scheme", out var ws) ? ws : null,
                Name = One(a, "name", required: false),
                BasePath = One(a, "base", required: false),
                BaseScheme = One(a, "base-scheme", required: false) ?? "NormalColor",
                ShellStylePath = One(a, "shellstyle", required: false),
                Wallpaper = One(a, "wallpaper", required: false) ?? XpThemeFiles.DefaultWallpaper,
                Write = a.ContainsKey("write"),
                PreferSource = a.TryGetValue("prefer-source", out var ps) ? ps : new List<string>(),
                Exclude = a.TryGetValue("exclude", out var ex) ? ex : new List<string>(),
            };
            if (a.ContainsKey("image-tolerance")) o.ImageTolerance = int.Parse(One(a, "image-tolerance"));

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
            return results.All(r => r.Ok) ? 0 : 1;
        }

        static int NoOp(string[] args)
        {
            var a = ParseArgs(args);
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
