using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace libmsstyle.Import
{
    public static class ImportReportWriter
    {
        /// <summary>writes &lt;prefix&gt;.json, &lt;prefix&gt;.md and &lt;prefix&gt;.threeway.json; returns the paths.</summary>
        public static List<string> Write(ImportReport r, string prefix)
        {
            var paths = new List<string> { prefix + ".json", prefix + ".md", prefix + ".threeway.json" };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(prefix)));
            // no BOM: the .md shows it as a stray character and not every json reader skips it.
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(paths[0], JsonWriter.Serialize(r), utf8);
            File.WriteAllText(paths[1], ToMarkdown(r), utf8);
            File.WriteAllText(paths[2], JsonWriter.Serialize(new { Summary = r.ThreeWaySummary, Entries = r.ThreeWayEntries }), utf8);
            return paths;
        }

        public static string ToMarkdown(ImportReport r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {r.Tool} report");
            sb.AppendLine();
            sb.AppendLine($"Status: **{r.Status}** ({(r.DryRun ? "dry run" : "write")}). {r.StatusText}");
            sb.AppendLine();
            sb.AppendLine("Offline checks only: the candidate was never applied to a live desktop, so it is unverified visually until tested in a disposable VM. A `uxtheme loads` line below means this process asked uxtheme to open the file, not that the style was drawn.");
            sb.AppendLine();
            sb.AppendLine("| input | path |");
            sb.AppendLine("|---|---|");
            foreach (var kv in r.Inputs) sb.AppendLine($"| {kv.Key} | `{kv.Value}` |");
            if (r.Output != null) sb.AppendLine($"| output | `{r.Output}` |");
            sb.AppendLine();

            // the importer copies the validation errors/warnings into the report lists with a "validation: "
            // prefix, and the same problems are printed again under "## Validation": show each one once.
            List("Errors", r.ErrorsWithoutValidation, sb);
            List("Warnings", r.WarningsWithoutValidation, sb);

            sb.AppendLine("## Counts");
            sb.AppendLine();
            sb.AppendLine("| what | count |");
            sb.AppendLine("|---|---:|");
            foreach (var kv in r.Counts.OrderBy(k => k.Key)) sb.AppendLine($"| {kv.Key} | {kv.Value} |");
            sb.AppendLine();

            sb.AppendLine("## Three-way comparison (keyed by class / part name / state / property)");
            sb.AppendLine();
            sb.AppendLine("| category | entries |");
            sb.AppendLine("|---|---:|");
            foreach (var kv in r.ThreeWaySummary.OrderBy(k => k.Key)) sb.AppendLine($"| {kv.Key} | {kv.Value} |");
            sb.AppendLine();
            sb.AppendLine("`source-changed`: source differs from its stock baseline. `template-adapted`: template differs from that baseline. `conflict`: both. `win7-only` (historical category name): no such source record in the template. Full list in the `.threeway.json` file.");
            sb.AppendLine();

            // only when the atlas was actually rebuilt: DwmImport sets NewAtlasSize in BuildAtlas, the last step
            // of a real import. An import that kept the template's DWMWindow (no source atlas, a non-stock
            // template atlas, an exclude rule) bails out before that and would otherwise print the defaults as
            // if they were verified facts. the reason it was skipped is already in the warnings.
            var d = r.Dwm;
            if (d.NewAtlasSize != null)
            {
                sb.AppendLine("## DWMWindow");
                sb.AppendLine();
                bool xpFrames = d.Parts.Any(p => p.Method == "xp-frame" || p.Method == "xp-button");
                if (xpFrames)
                    sb.AppendLine("- XP frame/button artwork synthesized into the template's unchanged DWM packing; stock Win7 atlas comparison is not applicable.");
                else
                {
                    sb.AppendLine($"- template atlas is the stock win7 atlas: {d.TemplateAtlasIsStockWin7}");
                    sb.AppendLine($"- source atlas differs from stock: {d.SourceAtlasChanged} ({d.SourceAtlasSize})");
                }
                sb.AppendLine($"- atlas stream: template {d.TemplateAtlasId} -> output {d.NewAtlasId} ({d.NewAtlasSize}{(d.AtlasReencoded ? ", re-encoded with kept template sprites" : ", source bytes unchanged")})");
                if (!xpFrames) sb.AppendLine($"- win7-only parts dropped: {(d.Win7OnlyParts.Count == 0 ? "none" : string.Join(", ", d.Win7OnlyParts))}");
                sb.AppendLine($"- template sprites kept (fallback): {(d.FallbackParts.Count == 0 ? "none" : string.Join(", ", d.FallbackParts.Distinct()))}");
                sb.AppendLine();
                if (d.Parts.Count > 0)
                {
                    sb.AppendLine("| id | template part | from win7 part | method | template rect | new rect | note |");
                    sb.AppendLine("|---:|---|---|---|---|---|---|");
                    foreach (var p in d.Parts)
                        sb.AppendLine($"| {p.TargetPartId} | {p.TargetPart} | {p.SourcePart} | {p.Method} | {Rect(p.TargetRect)} | {Rect(p.NewRect)} | {p.Note} |");
                    sb.AppendLine();
                }
            }

            var v = r.Validation ?? new ValidationReport();
            sb.AppendLine("## Validation");
            sb.AppendLine();
            // a blocked run never kept a candidate: its checks can't add up to a pass even when none failed.
            if (v.Blocked) sb.AppendLine("- blocked: the run stopped before keeping a candidate, so these checks are not a pass.");
            foreach (var c in v.Checks) sb.AppendLine("- ok: " + c);
            foreach (var e in v.Errors) sb.AppendLine("- FAIL: " + e);
            foreach (var w in v.Warnings) sb.AppendLine("- warn: " + w);
            sb.AppendLine();

            if (r.FinalStartPanelSizes.Count > 0)
            {
                sb.AppendLine("## Final StartPanel pane sizes");
                sb.AppendLine();
                sb.AppendLine("Values below are the final stored records after all import passes, not intermediate merge choices. RECT values are dimensions as consumed by the shell, including any negative origins.");
                sb.AppendLine();
                sb.AppendLine("| class | class id | part | part id | final value |");
                sb.AppendLine("|---|---:|---|---:|---|");
                foreach (var size in r.FinalStartPanelSizes.OrderBy(s => s.ClassId).ThenBy(s => s.PartId))
                    sb.AppendLine($"| {size.Class} | {size.ClassId} | {size.Part} | {size.PartId} | {size.Value} |");
                sb.AppendLine();
            }

            if (r.Resources.Count > 0)
            {
                var placements = r.Resources.GroupBy(x => x.Type + "/" + x.TargetId).Select(g => g.First()).ToList();
                int reused = placements.Count(x => x.Reused);
                int fresh = placements.Count - reused;
                int referenced = placements.Count(x => x.Referenced);
                r.Counts.TryGetValue("resources-added", out int queued);
                sb.AppendLine("## Resource placements");
                sb.AppendLine();
                sb.AppendLine($"{r.Resources.Count} source-to-output mappings, {placements.Count} distinct output resources: {fresh} allocated ids, {reused} reused ids. {referenced} output resources remain referenced by final property records; {placements.Count - referenced} placements have no final explicit reference. `resources-added` records {queued} queued resources, including resources generated outside this placement table.");
                sb.AppendLine();
                sb.AppendLine("| type | source id | output id | reused | size | bytes | final references |");
                sb.AppendLine("|---|---:|---:|---|---|---:|---:|");
                foreach (var m in r.Resources) sb.AppendLine($"| {m.Type} | {m.SourceId} | {m.TargetId} | {m.Reused} | {m.Dimensions} | {m.Bytes} | {m.FinalReferenceCount} |");
                sb.AppendLine();
            }

            List("Inheritance differences (win7 vs template, informational)", r.InheritanceDifferences, sb);
            List("States that keep template-only images (mostly 192-384 dpi variants the win7 source never had)", r.TemplateOnlyImages, sb);
            List("Source classes missing in the template", r.SourceClassesMissingInTarget, sb);
            List("Notes", r.Notes, sb);

            var interesting = r.Decisions.Where(x => x.Action != "applied").ToList();
            sb.AppendLine("## Decisions other than plain applies");
            sb.AppendLine();
            if (interesting.Count == 0) sb.AppendLine("none");
            else
            {
                sb.AppendLine("| class | part | state | property | action | reason | base | source | template | final result |");
                sb.AppendLine("|---|---|---:|---|---|---|---|---|---|---|");
                foreach (var x in interesting)
                    sb.AppendLine($"| {x.Class} | {x.Part} | {x.State} | {x.Property} | {x.Action} | {x.Reason} | {x.Base} | {x.Source} | {x.Target} | {x.Result} |");
            }
            sb.AppendLine();
            sb.AppendLine("Every decision, including applies, is in the `.json` report. Actions/reasons record merge history; Result is the final stored record after all passes (or absent/ambiguous), not a promise that an intermediate apply survived.");
            return sb.ToString();
        }

        private static string Rect(string s)
        {
            return s?.Replace("{", "").Replace("}", "");
        }

        private static void List(string title, List<string> items, StringBuilder sb)
        {
            if (items.Count == 0) return;
            sb.AppendLine("## " + title);
            sb.AppendLine();
            foreach (var i in items) sb.AppendLine("- " + i);
            sb.AppendLine();
        }
    }
}
