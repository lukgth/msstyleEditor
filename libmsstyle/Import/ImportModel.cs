using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace libmsstyle.Import
{
    public class Win7ImportOptions
    {
        /// <summary>the customized windows 7 (or, with <see cref="SourcePlatform"/> WinXP, windows xp) style.</summary>
        public string SourcePath { get; set; }
        /// <summary>unmodified stock style the source was made from: win7 aero or xp luna. Empty = the copy built into libmsstyle.</summary>
        public string BasePath { get; set; }
        /// <summary>Win7 (default; Vista styles too) or WinXP. Source and base must both be this generation.</summary>
        public Platform SourcePlatform { get; set; } = Platform.Win7;
        /// <summary>xp only: color scheme of the source to import (internal or display name). Empty = the first one.</summary>
        public string SourceScheme { get; set; }
        /// <summary>xp only: size of the source scheme to import ("LargeFonts"). Empty = NormalSize.</summary>
        public string SourceSize { get; set; }
        /// <summary>xp only: scheme of the base the template was ported from (NormalColor, Metallic, HomeStead). Empty = the first one.</summary>
        public string BaseScheme { get; set; }
        /// <summary>
        /// xp only: draw the task pane as a full-width band across the top of the folder window (xp's My Computer /
        /// My Documents WebView layout) instead of Windows 10's 200rp left pane. Off by default: the port keeps the
        /// left pane; pass --task-band to ask for the band.
        /// </summary>
        public bool TaskBand { get; set; } = false;
        /// <summary>xp only: the source's shellstyle.dll. Empty = Shell\&lt;SourceScheme&gt;\shellstyle.dll next to the source, if there is one.</summary>
        public string SourceShellStylePath { get; set; }
        /// <summary>known-working windows 10 style; the output is a modified copy of this file.</summary>
        public string TemplatePath { get; set; }
        /// <summary>where the candidate goes. Ignored for dry runs.</summary>
        public string OutputPath { get; set; }
        /// <summary>when true nothing is written next to OutputPath; the candidate is built in %TEMP%, validated and deleted.</summary>
        public bool DryRun { get; set; } = true;

        /// <summary>
        /// "Class|Part|Property" patterns (* wildcard, case-insensitive). On a conflict where the
        /// template changed a value away from stock win7, these take the source value anyway.
        /// </summary>
        public List<string> PreferSource { get; set; } = new List<string>();
        /// <summary>"Class|Part|Property" patterns that are never touched.</summary>
        public List<string> Exclude { get; set; } = new List<string>();

        /// <summary>
        /// Two pictures with equal size whose premultiplied channels differ by at most this much
        /// count as the same picture. Aero11 re-encoded most stock images with +-1..2 rounding noise;
        /// its real retouches (spin arrows, tooltip, header overflow) differ by 13 and more.
        /// </summary>
        public int ImageTolerance { get; set; } = 2;

        /// <summary>
        /// Style whose Shell folder and &lt;lang&gt;\*.msstyles.mui get copied next to the candidate.
        /// Empty = the template. The editor sets the opened file here, since its template is a temp snapshot.
        /// </summary>
        public string CompanionSourcePath { get; set; }
    }

    public static class KeyPattern
    {
        public static bool Matches(IEnumerable<string> patterns, string cls, string part, string prop)
        {
            string key = cls + "|" + part + "|" + prop;
            foreach (var p in patterns)
            {
                var parts = p.Split('|');
                string full = parts[0] + "|" + (parts.Length > 1 ? parts[1] : "*") + "|" + (parts.Length > 2 ? parts[2] : "*");
                string rx = "^" + Regex.Escape(full).Replace("\\*", ".*") + "$";
                if (Regex.IsMatch(key, rx, RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }
    }

    public enum ImportStatus
    {
        /// <summary>offline validation passed. Still unverified visually and not tested on a live system.</summary>
        ReadyForVmTest,
        /// <summary>something failed; no candidate file is kept.</summary>
        Blocked,
    }

    public class MergeDecision
    {
        public string Class { get; set; }
        public string Part { get; set; }
        public int State { get; set; }
        public string Property { get; set; }
        public string Action { get; set; }
        public string Reason { get; set; }
        public string Base { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public string Result { get; set; }
    }

    public class ResourceMapping
    {
        public string Type { get; set; }
        public int SourceId { get; set; }
        public int TargetId { get; set; }
        public bool Reused { get; set; }
        public int Bytes { get; set; }
        public string Sha1 { get; set; }
        public string Dimensions { get; set; }
    }

    public class DwmPartMapping
    {
        public int TargetPartId { get; set; }
        public string TargetPart { get; set; }
        public string SourcePart { get; set; }
        /// <summary>name, alias (matched by stock win7 atlas rect), fallback (template sprite kept), none.</summary>
        public string Method { get; set; }
        public string TargetRect { get; set; }
        public string NewRect { get; set; }
        public string Note { get; set; }
    }

    public class DwmReport
    {
        public bool TemplateAtlasIsStockWin7 { get; set; }
        public bool SourceAtlasChanged { get; set; }
        public int TemplateAtlasId { get; set; }
        public int NewAtlasId { get; set; }
        public string SourceAtlasSize { get; set; }
        public string NewAtlasSize { get; set; }
        public bool AtlasReencoded { get; set; }
        public List<DwmPartMapping> Parts { get; set; } = new List<DwmPartMapping>();
        public List<string> Win7OnlyParts { get; set; } = new List<string>();
        public List<string> FallbackParts { get; set; } = new List<string>();
    }

    public class ValidationReport
    {
        public bool Passed => Errors.Count == 0;
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Checks { get; set; } = new List<string>();
    }

    public class ImportReport
    {
        public string Tool { get; set; } = "msstyleEditor win7 importer";
        public string Generated { get; set; } = DateTime.UtcNow.ToString("u");
        public ImportStatus Status { get; set; }
        public string StatusText { get; set; }
        public bool DryRun { get; set; }
        public Dictionary<string, string> Inputs { get; set; } = new Dictionary<string, string>();
        public string Output { get; set; }
        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Notes { get; set; } = new List<string>();
        public DwmReport Dwm { get; set; } = new DwmReport();
        public List<ResourceMapping> Resources { get; set; } = new List<ResourceMapping>();
        public List<string> SourceClassesMissingInTarget { get; set; } = new List<string>();
        public List<string> InheritanceDifferences { get; set; } = new List<string>();
        /// <summary>states where source images were applied but the template has extra (mostly high dpi) images the source can't supply.</summary>
        public List<string> TemplateOnlyImages { get; set; } = new List<string>();
        public ValidationReport Validation { get; set; } = new ValidationReport();
        public List<MergeDecision> Decisions { get; set; } = new List<MergeDecision>();
        public Dictionary<string, int> ThreeWaySummary { get; set; } = new Dictionary<string, int>();
        /// <summary>full key-by-key comparison; written to its own file because it's large.</summary>
        [JsonIgnore]
        public List<DiffEntry> ThreeWayEntries { get; set; } = new List<DiffEntry>();

        public void Count(string what, int n = 1)
        {
            Counts.TryGetValue(what, out int c);
            Counts[what] = c + n;
        }

        /// <summary>adds <paramref name="format"/> (a {0} placeholder for the count) when any decision carries
        /// <paramref name="action"/>, and counts those decisions.</summary>
        public void AddNoteIfAny(string format, string action)
        {
            int n = Decisions.Count(d => string.Equals(d.Action, action, StringComparison.Ordinal));
            if (n == 0) return;
            Notes.Add(string.Format(format, n));
        }
    }

    public class DiffEntry
    {
        public string Category { get; set; }
        public string Class { get; set; }
        public string Part { get; set; }
        public int State { get; set; }
        public string Property { get; set; }
        public string Base { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
    }
}
