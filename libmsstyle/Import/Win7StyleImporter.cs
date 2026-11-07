using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace libmsstyle.Import
{
    /// <summary>
    /// Ports the look of a customized windows 7 style onto a known-working windows 10 style.
    ///
    /// Three inputs: the custom style (source), stock win7 aero (base, the common ancestor) and
    /// the win10 template. Only what the source changed relative to the base is carried over, and
    /// only where the template still has the stock value. Everything the template adapted for
    /// win10 stays. The output is a modified copy of the template file: class map, base class map,
    /// animation map and every unrelated resource are left byte for byte.
    /// </summary>
    public class Win7StyleImporter
    {
        public const string DwmClass = "DWMWindow";

        private readonly Win7ImportOptions m_opt;
        private readonly ImportReport m_report = new ImportReport();
        private SemanticModel m_base, m_source, m_target;
        private ResourceIdAllocator m_alloc;
        private readonly Dictionary<string, ArgbImage> m_decoded = new Dictionary<string, ArgbImage>();
        // properties already rewritten, so the generic pass can't touch DWM records twice.
        private readonly HashSet<StyleProperty> m_touched = new HashSet<StyleProperty>();
        // band pictures already reported as rebuilt, one note per source picture instead of per record.
        private readonly HashSet<string> m_bandStripsNoted = new HashSet<string>(StringComparer.Ordinal);

        internal List<SpriteExpectation> SpriteChecks { get; } = new List<SpriteExpectation>();
        internal Dictionary<StyleProperty, string> ChangedProperties { get; } = new Dictionary<StyleProperty, string>();

        public Win7StyleImporter(Win7ImportOptions options)
        {
            m_opt = options ?? throw new ArgumentNullException(nameof(options));
        }

        public static ImportReport Run(Win7ImportOptions options)
        {
            return new Win7StyleImporter(options).Execute();
        }

        public ImportReport Execute()
        {
            var r = m_report;
            bool xp = m_opt.SourcePlatform == Platform.WinXP;
            if (xp) r.Tool = "msstyleEditor xp importer";
            r.DryRun = m_opt.DryRun;
            r.Inputs["source"] = m_opt.SourcePath + (xp ? $" (scheme {m_opt.SourceScheme ?? "first"})" : "");
            r.Inputs["base"] = (string.IsNullOrEmpty(m_opt.BasePath) ? (xp ? "(built-in stock windows xp luna)" : "(built-in stock windows 7 aero)") : m_opt.BasePath)
                + (xp ? $" (scheme {m_opt.BaseScheme ?? "first"})" : "");
            r.Inputs["template"] = m_opt.TemplatePath;

            string candidate = null;
            try
            {
                if (string.IsNullOrEmpty(m_opt.BasePath)) m_opt.BasePath = xp ? StockStyles.XpLunaPath() : StockStyles.Win7AeroPath();
                CheckPaths();
                using (var baseStyle = new VisualStyle())
                using (var sourceStyle = new VisualStyle())
                using (var target = new VisualStyle())
                using (var targetInv = new ResourceInventory(m_opt.TemplatePath))
                {
                    LoadChecked(baseStyle, m_opt.BasePath, "base", m_opt.SourcePlatform, m_opt.BaseScheme);
                    LoadChecked(sourceStyle, m_opt.SourcePath, "source", m_opt.SourcePlatform, m_opt.SourceScheme);
                    LoadChecked(target, m_opt.TemplatePath, "template", Platform.Win10, null);
                    if (r.Errors.Count > 0)
                    {
                        return Finish(null);
                    }
                    AlignClassNames(sourceStyle, target);
                    AlignClassNames(baseStyle, target);
                    if (xp) new XpStripRemap(baseStyle, sourceStyle, target, r).Run();

                    m_source = new SemanticModel(sourceStyle);
                    m_base = new SemanticModel(baseStyle);
                    m_target = new SemanticModel(target);
                    if (baseStyle.StringTables.All(t => t.Value.Count == 0))
                    {
                        // stock win7 keeps its strings in a .mui that isn't shipped with the file.
                        r.Notes.Add("base style has no string table, so string/font properties are compared by id: a source that edits the text of an unchanged id isn't detected.");
                    }

                    // an id with several language variants can't be shared by content: the bytes the
                    // loader picks depend on the language, so only single-language ids are reused.
                    var multiLang = new HashSet<string>(targetInv.Entries.GroupBy(e => e.Type + "/" + e.Name).Where(g => g.Count() > 1).Select(g => g.Key));
                    m_alloc = new ResourceIdAllocator(targetInv.Entries
                        .Where(e => e.Type == "IMAGE" || e.Type == "STREAM")
                        .Select(e =>
                        {
                            e.TryGetIntName(out int id);
                            return Tuple.Create(e.Type, id, multiLang.Contains(e.Type + "/" + e.Name) ? null : e.Sha1);
                        }));

                    CompareClasses();
                    BuildThreeWayDiff();
                    new DwmImport(this).Run();
                    var shell = xp ? OpenShellStyles() : null;
                    shell?.Snapshot();
                    MergeGenericClasses();
                    shell?.Run();
                    m_headerMargins = shell?.HeaderContentMargins();
                    m_hoverRecords = shell?.HoverRecords;
                    m_paneRecord = shell?.PaneRecord;
                    m_watermarkRecord = shell?.Watermarks;
                    m_noSectionIcons = shell != null && !shell.HasSectionIcons;
                    ReportTemplateOnlyImages();
                    CollectFrameWaivers();

                    foreach (var add in m_alloc.Added)
                    {
                        var parts = add.Key.Split('/');
                        int id = int.Parse(parts[1]);
                        target.QueueResourceData(id, parts[0] == "IMAGE" ? StyleResourceType.Image : StyleResourceType.Atlas, add.Value);
                    }
                    r.Count("resources-added", m_alloc.Added.Count);

                    // validate in a folder of its own, with Shell and the .mui next to it, named like the
                    // final file: string tables of mui-based templates only resolve that way.
                    string name = Path.GetFileName(m_opt.DryRun ? m_opt.TemplatePath : m_opt.OutputPath);
                    m_validationDir = m_opt.DryRun
                        ? Path.Combine(Path.GetTempPath(), "msstyle-import-" + Guid.NewGuid().ToString("N"))
                        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(m_opt.OutputPath)), ".msstyle-import-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(m_validationDir);
                    candidate = Path.Combine(m_validationDir, name);
                    // added font strings only reach the file through the standalone path, which
                    // writes the string tables into the .msstyles (and drops a .mui link, if any).
                    target.Save(candidate, StringsAdded);
                    StyleCompanions.CopyAlongside(CompanionSource, candidate);
                }

                r.Validation = new StyleValidator(m_opt.TemplatePath, candidate, m_alloc.Added, SpriteChecks, ChangedProperties.Values.ToList(),
                    StringsAdded, GeometryWaivers, AddedRecords, FrameWaivers, RemovedRecords).Validate();
                foreach (var e in r.Validation.Errors) r.Errors.Add("validation: " + e);
                foreach (var w in r.Validation.Warnings) r.Warnings.Add("validation: " + w);
                return Finish(candidate);
            }
            catch (Exception ex)
            {
                r.Errors.Add("import aborted: " + ex.Message);
                return Finish(candidate);
            }
        }

        private string m_validationDir;

        private XpShellStyle m_srcShell, m_baseShell;
        // the port's ExplorerBar header CONTENTMARGINS (true = main section), for the shellstyle layout patch.
        private Dictionary<bool, Margins> m_headerMargins;
        // ExplorerBar records holding the source's task hover picture (true = main section), for the same patch.
        private Dictionary<bool, (int Part, int State)> m_hoverRecords;
        // ExplorerBar record holding the source's task pane background picture, for the same patch.
        private (int Part, int State)? m_paneRecord;
        // ExplorerBar record holding the source's folder watermark and its height, for the same patch.
        private XpShellImport.PaneWatermarkSet m_watermarkRecord;
        private bool m_noSectionIcons;

        /// <summary>
        /// source and stock shellstyle for this scheme. A source without Shell\&lt;scheme&gt;\shellstyle.dll is drawn
        /// on xp with %SystemRoot%\System32\shellstyle.dll (shell32 SHGetShellStyleHInstance falls back to it),
        /// the classic one: system color names, resolved here with the source's own SysMetrics colors.
        /// </summary>
        private XpShellImport OpenShellStyles()
        {
            string src = m_opt.SourceShellStylePath ?? XpShellImport.FindShellStyle(m_opt.SourcePath, m_opt.SourceScheme ?? "NormalColor");
            IReadOnlyDictionary<string, Color> sysColors = null;
            if (src == null)
            {
                src = StockStyles.XpClassicShellPath();
                sysColors = SourceSysColors();
                m_report.Notes.Add($"shellstyle: no Shell\\{m_opt.SourceScheme ?? "NormalColor"}\\shellstyle.dll next to the source; xp draws its task pane with the classic System32 shellstyle in the theme's system colors, so that's the source shellstyle.");
            }
            try
            {
                m_srcShell = new XpShellStyle(src, sysColors);
                m_baseShell = new XpShellStyle(StockStyles.XpLunaShellPath(m_opt.BaseScheme));
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException)
            {
                m_report.Warnings.Add($"shellstyle \"{src}\" not used: {ex.Message}");
                return null;
            }
            m_report.Inputs["shellstyle"] = src;
            return new XpShellImport(this, m_srcShell, m_baseShell, classicFallback: sysColors != null);
        }

        // DUI color names (directui parser.cpp _sct) -> the SysMetrics color they read, and the value xp uses when
        // the theme leaves it out (uxtheme loader.cpp DefaultColors; matches GetSysColor on the xp vm under Flarn).
        private static readonly (string Name, IDENTIFIER Id, Color Default)[] DuiSysColors =
        {
            ("window", IDENTIFIER.WINDOW, Color.FromArgb(255, 255, 255)), ("windowtext", IDENTIFIER.WINDOWTEXT, Color.FromArgb(0, 0, 0)),
            ("buttonface", IDENTIFIER.BTNFACE, Color.FromArgb(212, 208, 200)), ("buttontext", IDENTIFIER.BTNTEXT, Color.FromArgb(0, 0, 0)),
            ("activecaption", IDENTIFIER.ACTIVECAPTION, Color.FromArgb(10, 36, 106)), ("captiontext", IDENTIFIER.CAPTIONTEXT, Color.FromArgb(255, 255, 255)),
            ("inactivecaption", IDENTIFIER.INACTIVECAPTION, Color.FromArgb(128, 128, 128)), ("highlight", IDENTIFIER.HIGHLIGHT, Color.FromArgb(10, 36, 106)),
            ("highlighttext", IDENTIFIER.HIGHLIGHTTEXT, Color.FromArgb(255, 255, 255)), ("graytext", IDENTIFIER.GRAYTEXT, Color.FromArgb(128, 128, 128)),
            ("buttonshadow", IDENTIFIER.BTNSHADOW, Color.FromArgb(128, 128, 128)), ("buttonhighlight", IDENTIFIER.BTNHIGHLIGHT, Color.FromArgb(255, 255, 255)),
        };

        private Dictionary<string, Color> SourceSysColors()
        {
            var d = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            m_source.ClassesByName.TryGetValue("sysmetrics", out var sc);
            foreach (var (name, id, def) in DuiSysColors)
                d[name] = sc != null && SemanticModel.FindEffective(m_source.Style, sc, 0, 0, id)?.GetValue() is Color c ? c : def;
            return d;
        }

        /// <summary>the copied win10 shellstyle (Shell\NormalColor, the only folder the .theme names) gets the source's bitmaps.</summary>
        private void PatchShellBitmaps()
        {
            if (m_srcShell == null) return;
            string target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(m_opt.OutputPath)), "Shell", "NormalColor", "shellstyle.dll");
            if (!File.Exists(target)) return;
            try
            {
                var ids = XpShellImport.PatchShellStyleBitmaps(target, m_srcShell, m_baseShell, false);
                if (ids.Count > 0)
                    m_report.Notes.Add($"shellstyle: replaced bitmaps {string.Join(", ", ids)} in Shell\\NormalColor\\shellstyle.dll with the source shellstyle's.");
                else
                    m_report.Notes.Add("shellstyle: " + ShellIdentity(m_srcShell, m_baseShell)
                        + "; nothing to replace, so Shell\\NormalColor keeps the port's own bitmaps, which are that same stock art transcribed for win10.");
                m_report.Count("shellstyle-bitmaps-replaced", ids.Count);
                m_report.AddNoteIfAny("shellstyle: {0} ExplorerBar records follow the source shellstyle. The win10 task pane is drawn from msstyles ExplorerBar records, and the port transcribed stock xp's shellstyle into them; where the source shellstyle resolves to that stock art the template's transcription stays.", "restored-shellstyle");
                var (notes, warnings) = XpShellImport.PatchShellStyleLayout(target, m_srcShell, m_baseShell, m_headerMargins, m_hoverRecords, m_paneRecord, m_watermarkRecord, false, m_opt.TaskBand, m_noSectionIcons);
                m_report.Notes.AddRange(notes);
                m_report.Warnings.AddRange(warnings);
            }
            catch (IOException ex)
            {
                m_report.Warnings.Add("shellstyle bitmaps not replaced: " + ex.Message);
            }
        }
        /// <summary>what the source shellstyle actually is, for the report: a pack can ship stock luna's file verbatim
        /// and the "0 bitmaps replaced" line then looks like a failure when it is the whole story.</summary>
        private static string ShellIdentity(XpShellStyle source, XpShellStyle stock)
        {
            try
            {
                if (source.Path != null && stock.Path != null && File.Exists(source.Path) && File.Exists(stock.Path))
                {
                    var a = File.ReadAllBytes(source.Path);
                    var b = File.ReadAllBytes(stock.Path);
                    string sha = ResourceInventory.Sha1Hex(a);
                    if (a.SequenceEqual(b))
                        return $"the source shellstyle is byte-identical to stock xp luna's (sha1 {sha})";
                    return $"the source shellstyle has none of the bitmaps stock xp luna lacks (sha1 {sha})";
                }
            }
            catch (IOException) { }
            return "the source shellstyle's bitmaps are all stock xp's";
        }

        private string CompanionSource => string.IsNullOrEmpty(m_opt.CompanionSourcePath) ? m_opt.TemplatePath : m_opt.CompanionSourcePath;

        private ImportReport Finish(string candidate)
        {
            var r = m_report;
            r.Status = r.Errors.Count == 0 ? ImportStatus.ReadyForVmTest : ImportStatus.Blocked;
            if (candidate != null && File.Exists(candidate) && r.Status == ImportStatus.ReadyForVmTest && !m_opt.DryRun)
            {
                if (File.Exists(m_opt.OutputPath)) File.Delete(m_opt.OutputPath);
                File.Move(candidate, m_opt.OutputPath);
                r.Output = m_opt.OutputPath;
                try
                {
                    var copied = StyleCompanions.CopyAlongside(CompanionSource, m_opt.OutputPath);
                    r.Notes.Add(copied.Count > 0
                        ? "copied next to the candidate (windows needs them to load the shellstyle and strings): " + string.Join(", ", copied)
                        : $"no Shell folder or .mui next to \"{CompanionSource}\"; without a Shell folder windows can't load a shellstyle for this style.");
                    PatchShellBitmaps();
                }
                catch (Exception ex)
                {
                    r.Warnings.Add("couldn't copy the Shell / language folders next to the candidate: " + ex.Message);
                }
            }
            if (m_validationDir != null)
            {
                try { Directory.Delete(m_validationDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            m_srcShell?.Dispose();
            m_baseShell?.Dispose();

            if (r.Status == ImportStatus.Blocked)
                r.StatusText = "blocked: offline validation or import failed, no candidate was kept.";
            else if (m_opt.DryRun)
                r.StatusText = "dry run passed offline validation. nothing was written; rerun without dry run to get the candidate.";
            else
                r.StatusText = "candidate passed offline validation. unverified visually and never applied: test it in a disposable win10 vm with a snapshot before using it anywhere else.";
            return r;
        }

        private void CheckPaths()
        {
            foreach (var p in new[] { m_opt.SourcePath, m_opt.BasePath, m_opt.TemplatePath })
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p))
                    throw new FileNotFoundException($"input style not found: \"{p}\"");
            }
            if (m_opt.DryRun) return;
            if (string.IsNullOrEmpty(m_opt.OutputPath))
                throw new ArgumentException("no output path given");

            string outFull = Path.GetFullPath(m_opt.OutputPath);
            string outDir = Path.GetDirectoryName(outFull);
            foreach (var p in new[] { m_opt.SourcePath, m_opt.BasePath, m_opt.TemplatePath })
            {
                string full = Path.GetFullPath(p);
                if (string.Equals(full, outFull, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"output \"{outFull}\" would overwrite an input");
                if (string.Equals(Path.GetDirectoryName(full), outDir, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"output directory \"{outDir}\" holds an input style; pick a separate scratch directory");
            }
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win) && outFull.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("refusing to write below the windows directory");
            Directory.CreateDirectory(outDir);
        }

        private void LoadChecked(VisualStyle style, string path, string role, Platform expected, string xpScheme)
        {
            style.Load(path);
            foreach (var d in style.LoadDiagnostics)
            {
                m_report.Errors.Add($"{role} \"{path}\" has property stream damage: {d}");
            }
            bool ok = expected == Platform.WinXP ? style.Platform == Platform.WinXP
                : expected == Platform.Win7 ? style.Platform == Platform.Win7 || style.Platform == Platform.Vista
                : style.Platform == Platform.Win10 || style.Platform == Platform.Win11;
            if (!ok)
            {
                m_report.Errors.Add($"{role} \"{path}\" was detected as {style.Platform.ToDisplayString()}, expected {expected.ToDisplayString()}.");
                return;
            }
            if (style.Platform == Platform.WinXP)
            {
                if (!string.IsNullOrEmpty(xpScheme))
                {
                    var scheme = style.XpColorSchemes.FirstOrDefault(s => s.InternalName.Equals(xpScheme, StringComparison.OrdinalIgnoreCase))
                        ?? style.XpColorSchemes.FirstOrDefault(s => s.DisplayName.Equals(xpScheme, StringComparison.OrdinalIgnoreCase));
                    if (scheme == null)
                    {
                        m_report.Errors.Add($"{role} \"{path}\" has no color scheme \"{xpScheme}\" (has: {string.Join(", ", style.XpColorSchemes.Select(s => s.InternalName))}).");
                        return;
                    }
                    XpSize size = null;
                    if (role == "source" && !string.IsNullOrEmpty(m_opt.SourceSize) && !m_opt.SourceSize.Equals("NormalSize", StringComparison.OrdinalIgnoreCase))
                    {
                        size = scheme.Sizes.FirstOrDefault(z => z.InternalName.Equals(m_opt.SourceSize, StringComparison.OrdinalIgnoreCase));
                        if (size == null)
                        {
                            m_report.Errors.Add($"{role} scheme {scheme.InternalName} has no size \"{m_opt.SourceSize}\" (has: NormalSize{string.Concat(scheme.Sizes.Select(z => ", " + z.InternalName))}).");
                            return;
                        }
                    }
                    if (scheme != style.XpColorScheme || size != style.XpSize) style.LoadXpWithScheme(scheme, size);
                }
                // the same INI lines xp's own parser rejects; skipped, so the import matches what xp drew.
                foreach (var d in style.XpDiagnostics)
                    m_report.Warnings.Add($"{role} ini: {d}");
                m_report.Notes.Add($"{role}: color scheme {style.XpColorScheme} ({style.XpSize?.IniResourceName ?? style.XpColorScheme.IniResourceName}).");
            }
            m_report.Notes.Add($"{role}: {style.Platform.ToDisplayString()}, {style.Classes.Count} classes, {style.NumProperties} properties.");
        }

        /// <summary>
        /// XP class names are case-insensitive ("button", "SysMetrics"), the semantic keys aren't:
        /// classes the template also has take the template's spelling.
        /// </summary>
        private static void AlignClassNames(VisualStyle style, VisualStyle template)
        {
            if (style.Platform != Platform.WinXP) return;
            var names = template.Classes.Values.GroupBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().ClassName, StringComparer.OrdinalIgnoreCase);
            foreach (var c in style.Classes.Values)
            {
                if (names.TryGetValue(c.ClassName, out var n)) c.ClassName = n;
            }
        }

        /// <summary>
        /// win10 templates often add IMAGEFILE4-7 (192-384 dpi) next to the win7 96-144 dpi set.
        /// The source has nothing for those, so at high dpi such a part still draws the template
        /// picture. Not an error, but the user should know which parts are mixed.
        /// </summary>
        private void ReportTemplateOnlyImages()
        {
            var byProp = m_target.Entries.ToDictionary(e => e.Property);
            var changedStates = new HashSet<string>(ChangedProperties.Keys
                .Where(p => byProp.TryGetValue(p, out var e) && e.ResourceType == "IMAGE")
                .Select(p => byProp[p].StateKey));
            var mixed = m_target.Entries
                .Where(e => e.ResourceType == "IMAGE" && changedStates.Contains(e.StateKey)
                    && !m_touched.Contains(e.Property) && !m_base.ByKey.ContainsKey(e.Key))
                .GroupBy(e => e.StateKey)
                .ToList();
            foreach (var g in mixed)
            {
                m_report.TemplateOnlyImages.Add($"{g.Key}: {string.Join(", ", g.Select(e => e.PropertyName))}");
            }
            m_report.Count("states-with-template-only-images", mixed.Count);
        }

        /// <summary>
        /// Key-by-key comparison of all three styles, before anything is modified. Keys are
        /// class|part name|state|property#type, never numeric class/part ids.
        /// </summary>
        private void BuildThreeWayDiff()
        {
            var r = m_report;
            var keys = new HashSet<string>(m_base.ByKey.Keys);
            keys.UnionWith(m_source.ByKey.Keys);
            keys.UnionWith(m_target.ByKey.Keys);
            foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                m_base.ByKey.TryGetValue(key, out var b);
                m_source.ByKey.TryGetValue(key, out var s);
                m_target.ByKey.TryGetValue(key, out var a);
                var any = b ?? s ?? a;
                var cats = new List<string>();
                if (b != null && s != null && !Same(m_base, b, m_source, s)) cats.Add("source-changed");
                if (b == null && s != null) cats.Add("source-only");
                if (b != null && s == null) cats.Add("source-removed");
                if (b != null && a != null && !Same(m_base, b, m_target, a)) cats.Add("template-adapted");
                if (b == null && s == null && a != null) cats.Add("template-only");
                if (b != null && a == null) cats.Add("win7-only");
                if (!any.PartNameKnown) cats.Add("numeric-part");
                if (cats.Count == 0 || (cats.Count == 1 && cats[0] == "numeric-part"))
                {
                    r.ThreeWaySummary.TryGetValue("identical", out int n);
                    r.ThreeWaySummary["identical"] = n + 1;
                    continue;
                }
                foreach (var c in cats)
                {
                    r.ThreeWaySummary.TryGetValue(c, out int n);
                    r.ThreeWaySummary[c] = n + 1;
                }
                if (cats.Contains("source-changed") && cats.Contains("template-adapted"))
                {
                    r.ThreeWaySummary.TryGetValue("conflict", out int n);
                    r.ThreeWaySummary["conflict"] = n + 1;
                }
                r.ThreeWayEntries.Add(new DiffEntry
                {
                    Category = string.Join(",", cats),
                    Class = any.ClassName,
                    Part = any.PartName,
                    State = any.StateId,
                    Property = any.PropertyName + "#" + any.TypeId + (any.Occurrence > 0 ? "@" + any.Occurrence : ""),
                    Base = b?.Value == b?.RawValue ? b?.RawValue : $"{b?.RawValue} ({b?.Value})",
                    Source = s?.Value == s?.RawValue ? s?.RawValue : $"{s?.RawValue} ({s?.Value})",
                    Target = a?.Value == a?.RawValue ? a?.RawValue : $"{a?.RawValue} ({a?.Value})",
                });
            }
        }

        private void CompareClasses()
        {
            foreach (var name in m_source.ClassesByName.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (!m_target.ClassesByName.ContainsKey(name))
                    m_report.SourceClassesMissingInTarget.Add(name);
            }
            foreach (var dup in m_target.DuplicateClassNames)
            {
                m_report.Warnings.Add($"template has class \"{dup}\" more than once; only the first is addressed by name.");
            }

            foreach (var kv in m_base.ClassesByName)
            {
                if (!m_target.ClassesByName.TryGetValue(kv.Key, out var t)) continue;
                string b = BaseName(m_base, kv.Value), tb = BaseName(m_target, t);
                if (!string.Equals(b, tb, StringComparison.OrdinalIgnoreCase))
                    m_report.InheritanceDifferences.Add($"{kv.Key}: win7 base \"{b ?? "-"}\", template base \"{tb ?? "-"}\" (template kept)");
                string s = m_source.ClassesByName.TryGetValue(kv.Key, out var sc) ? BaseName(m_source, sc) : null;
                if (!string.Equals(b, s, StringComparison.OrdinalIgnoreCase))
                    m_report.Warnings.Add($"source changed the base class of {kv.Key} from \"{b ?? "-"}\" to \"{s ?? "-"}\"; inheritance changes aren't imported.");
            }
        }

        private static string BaseName(SemanticModel m, StyleClass c)
        {
            return c.BaseClassId >= 0 && m.Style.Classes.TryGetValue(c.BaseClassId, out var b) ? b.ClassName : null;
        }

        // ---- generic three-way merge -------------------------------------------------------

        private void MergeGenericClasses()
        {
            var keys = new HashSet<string>(m_source.ByKey.Keys);
            keys.UnionWith(m_base.ByKey.Keys);
            var locked = LockedGeometryParts(keys);
            m_sourceGeometryParts = new HashSet<string>(m_scalarChanges.Where(p => !locked.ContainsKey(p)), StringComparer.Ordinal);

            foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                m_base.ByKey.TryGetValue(key, out var b);
                m_source.ByKey.TryGetValue(key, out var s);
                var any = s ?? b;
                if (string.Equals(any.ClassName, DwmClass, StringComparison.OrdinalIgnoreCase)) continue;
                if (m_target.DuplicateClassNames.Contains(any.ClassName)) 
                {
                    if (s != null && b != null && !Same(m_base, b, m_source, s))
                        Decide(any, "skipped-ambiguous-class", "class name occurs more than once in the template", b, s, null, null);
                    continue;
                }
                m_target.ByKey.TryGetValue(key, out var a);
                if (a != null && b != null && s != null && GeometryIds.Contains(any.NameId) && locked.TryGetValue(any.PartKey, out string why)
                    && !Same(m_base, b, m_source, s) && !Same(m_source, s, m_target, a))
                {
                    Decide(s, "kept-target", why, b, s, a, null);
                    m_report.Count("kept-target-geometry-group");
                    continue;
                }
                ThreeWay(any.ClassName, any.PartName, any.StateId, any.PropertyName, b, s, a, m_source, any.PartNameKnown);
            }

            if (m_source.Platform == Platform.WinXP) FollowMovedImages(locked);
            PropagateImages(locked);
            FillDpiSlots(locked);
            CopyIntoTemplateOverrides();
            MirrorIntoWin10Classes();
            if (m_source.Platform == Platform.WinXP) FillFlatFromXp();
            if (m_source.Platform == Platform.WinXP) SingleImageIntoDpiSlots();
            if (m_source.Platform == Platform.WinXP) GlyphSizeSlotsFromXp();
            if (m_source.Platform == Platform.WinXP) ThemeWholeClientEdge();
            if (m_source.Platform == Platform.WinXP) OpaqueSpinBackgrounds();
            if (m_source.Platform == Platform.WinXP) MenuColorsFromXp();
            if (m_source.Platform == Platform.WinXP) TransparentForTrueAlpha();
            if (m_source.Platform == Platform.WinXP) SizingTypeFromXp();
            if (m_source.Platform == Platform.WinXP) DropGlyphlessXpButtonDpi();
            if (m_source.Platform == Platform.WinXP) FrameXpCombo();
            if (m_source.Platform == Platform.WinXP) FitXpStartPanel();
        }

        /// <summary>
        /// xp only. The template's SIZINGTYPE belongs to the template's own picture: Luna10 redrew
        /// Progress|CHUNK as a stretched pill, while xp tiled the source's 18x18 hatch art (TangoLux's
        /// progress fill smeared into a few wide diagonal bands). A part that ends up drawing the
        /// source's picture with the source's SIZINGMARGINS draws the source art the way xp did, so it
        /// keeps xp's TILE/STRETCH. Parts still on a template picture or template margins are left alone.
        /// </summary>
        private void SizingTypeFromXp()
        {
            var imageCache = new Dictionary<int, ImageInfo>();
            foreach (var tc in m_target.Style.Classes.Values)
            {
                if (string.Equals(tc.ClassName, DwmClass, StringComparison.OrdinalIgnoreCase)) continue;
                if (m_target.DuplicateClassNames.Contains(tc.ClassName)) continue;
                if (!m_source.ClassesByName.TryGetValue(tc.ClassName, out var sc)) continue;

                var tnames = SemanticModel.PartNames(tc.ClassName, Platform.Win10);
                var snames = SemanticModel.PartNames(sc.ClassName, Platform.WinXP);
                foreach (var tp in tc.Parts.Values)
                {
                    if (!tnames.TryGetValue(tp.PartId, out string partName)) continue;
                    int? spid = null;
                    foreach (var kv in snames)
                        if (string.Equals(kv.Value, partName, StringComparison.OrdinalIgnoreCase)) { spid = kv.Key; break; }
                    // win10 mirrors a few button parts for right-to-left (Combobox DROPDOWNBUTTONRIGHT/LEFT); xp has
                    // the one part, and a style needn't declare the mirrored ids, so a part the source lacks falls
                    // back to the name it mirrors.
                    if ((spid == null || !sc.Parts.ContainsKey(spid.Value))
                        && (partName.EndsWith("RIGHT", StringComparison.Ordinal) || partName.EndsWith("LEFT", StringComparison.Ordinal)))
                    {
                        string stem = partName.Substring(0, partName.Length - (partName.EndsWith("RIGHT", StringComparison.Ordinal) ? 5 : 4));
                        foreach (var kv in snames)
                            if (string.Equals(kv.Value, stem, StringComparison.OrdinalIgnoreCase) && sc.Parts.ContainsKey(kv.Key))
                            { spid = kv.Key; break; }
                    }
                    if (spid == null) continue;

                    if (!sc.Parts.TryGetValue(spid.Value, out var spart)) continue;
                    foreach (var st in tp.States.Values)
                    {
                        if (!spart.States.TryGetValue(st.StateId, out var sstate)) continue;
                        var tSizing = st.Properties.FirstOrDefault(p => p.Header.nameID == (int)IDENTIFIER.SIZINGTYPE
                            && p.Header.typeID == (int)IDENTIFIER.ENUM);
                        if (tSizing == null) continue;
                        bool sourceImage = sstate.Properties.Any(p => ImageNames.Contains((IDENTIFIER)p.Header.nameID)
                            && VisualStyle.XpImageIds.Contains(p.Header.nameID));
                        if (!sourceImage) continue;
                        var sSizing = SemanticModel.FindEffective(m_source.Style, sc, spid.Value, st.StateId, IDENTIFIER.SIZINGTYPE);
                        if (sSizing == null) continue;
                        if (!(tSizing.GetValue() is int tv) || !(sSizing.GetValue() is int sv) || tv == sv) continue;
                        if (KeyPattern.Matches(m_opt.Exclude, tc.ClassName, partName, "SIZINGTYPE")) continue;
                        if (!PicturesFromSource(sc, tc, spid.Value, tp.PartId, st.StateId, imageCache)) continue;
                        if (!MarginsFromSource(sc, tc, spid.Value, tp.PartId, st.StateId)) continue;

                        tSizing.SetValue(sv);
                        MarkTouched(tSizing, $"{tc.ClassName}|{partName}|S{st.StateId}|SIZINGTYPE");
                        m_report.Decisions.Add(new MergeDecision
                        {
                            Class = tc.ClassName,
                            Part = partName,
                            State = st.StateId,
                            Property = "SIZINGTYPE",
                            Action = "applied-xp-sizingtype",
                            Reason = "the part draws the source's picture with the source's sizing margins, so xp's TILE/STRETCH goes with it; the template's value was fitted to the template's own art",
                            Result = SizingTypeName(sv),
                        });
                        m_report.Count("applied-xp-sizingtype");
                    }
                }
            }
        }

        /// <summary>
        /// xp only. win10 draws the combo button two ways: from a dpi-selected glyph set (IMAGEFILE1-7,
        /// picked by IMAGESELECTTYPE; Luna10's arrow works that way, the button plate in IMAGEFILE and
        /// the arrow in GLYPHIMAGEFILE), or from the single IMAGEFILE picture. Elfin2's xp button is one
        /// 10x10 arrow per state drawn TRUESIZE, with an empty GLYPHIMAGEFILE. The port copied that
        /// picture into the win10 button parts (the address bar reads DROPDOWNBUTTONRIGHT) but left the
        /// template's dpi selection on, so uxtheme re-cut the arrow strip as a dpi glyph: a broken arrow
        /// almost twice xp's size. A source part without a glyph of its own that already draws the
        /// source's picture turns the dpi selection off, so uxtheme draws the picture with the source's
        /// SIZINGTYPE/IMAGELAYOUT/IMAGECOUNT (SizingTypeFromXp covers the sizing type).
        /// </summary>
        private void DropGlyphlessXpButtonDpi()
        {
            if (!m_target.ClassesByName.TryGetValue("Combobox", out var tc) || m_target.DuplicateClassNames.Contains("Combobox")
                || !m_source.ClassesByName.TryGetValue("Combobox", out var sc)) return;
            int spid = -1;
            foreach (var kv in SemanticModel.PartNames(sc.ClassName, Platform.WinXP))
                if (string.Equals(kv.Value, "DROPDOWNBUTTON", StringComparison.OrdinalIgnoreCase)) { spid = kv.Key; break; }
            if (spid < 0) return;
            var glyph = SemanticModel.FindEffective(m_source.Style, sc, spid, 0, IDENTIFIER.GLYPHIMAGEFILE);
            if (glyph != null)
            {
                // a real glyph: the template's dpi glyph set is how win10 draws it (FillDpiSlots feeds it).
                var glyphImage = Decoded(m_source, "IMAGE", glyph.Header.shortFlag & 0xFFFF);
                if (glyphImage != null && glyphImage.Pixels.Any(px => (px >> 24) != 0)) return;
            }

            var imageCache = new Dictionary<int, ImageInfo>();
            var tnames = SemanticModel.PartNames(tc.ClassName, Platform.Win10);
            foreach (var tp in tc.Parts.Values)
            {
                if (!tnames.TryGetValue(tp.PartId, out string partName)) continue;
                if (!string.Equals(partName, "DROPDOWNBUTTON", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(partName, "DROPDOWNBUTTONRIGHT", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(partName, "DROPDOWNBUTTONLEFT", StringComparison.OrdinalIgnoreCase)) continue;
                if (!tp.States.TryGetValue(0, out var st)) continue;
                var select = st.Properties.FirstOrDefault(p => p.Header.nameID == (int)IDENTIFIER.IMAGESELECTTYPE && p.Header.typeID == (int)IDENTIFIER.ENUM);
                if (select == null || !(select.GetValue() is int current) || current == (int)0) continue;
                if (KeyPattern.Matches(m_opt.Exclude, tc.ClassName, partName, "IMAGESELECTTYPE")) continue;
                if (!PicturesFromSource(sc, tc, spid, tp.PartId, 0, imageCache)) continue;
                select.SetValue((int)0);
                MarkTouched(select, $"{tc.ClassName}|{partName}|S{st.StateId}|IMAGESELECTTYPE");
                m_report.Decisions.Add(new MergeDecision
                {
                    Class = tc.ClassName,
                    Part = partName,
                    State = st.StateId,
                    Property = "IMAGESELECTTYPE",
                    Action = "dropped-xp-button-dpi",
                    Reason = "the source's button is one picture with no glyph of its own and this part draws that picture; win10's dpi selection would cut it out of the template's glyph set instead",
                    Result = "NONE",
                });
                m_report.Count("dropped-xp-button-dpi");
            }
        }

        /// <summary>
        /// xp only. xp paints the combo frame from Combobox|0 as a nine-slice picture (Elfin2's 12x25
        /// plate: a 193 grey edge around a 206 face). win10 draws the frame from Combobox|4 BORDER as a
        /// border fill and reads the fill from the class colors, so that plate is never drawn and the
        /// source's *fallback* colors - the pair xp uses only when no plate is set (Elfin2: 0,0,0 edge
        /// and 140,140,140 face) - painted a black box with a dark grey face on the address bar. The
        /// plate's edge and face colors go onto the colors win10 reads. A theme whose Combobox|0 is
        /// already a BORDERFILL (stock Luna's blue edge on white) carries its colors directly and is
        /// left alone.
        /// </summary>
        private void FrameXpCombo()
        {
            if (!m_target.ClassesByName.TryGetValue("Combobox", out var tc) || m_target.DuplicateClassNames.Contains("Combobox")
                || !m_source.ClassesByName.TryGetValue("Combobox", out var sc)) return;
            if (!(SemanticModel.FindEffective(m_source.Style, sc, 0, 0, IDENTIFIER.BGTYPE)?.GetValue() is int bg) || bg != (int)0) return;
            var img = SemanticModel.FindEffective(m_source.Style, sc, 0, 0, IDENTIFIER.IMAGEFILE);
            if (img == null || !(img.GetValue() is int id)) return;
            var plate = Decoded(m_source, "IMAGE", id & 0xFFFF);
            if (plate == null || plate.Width < 3 || plate.Height < 3) return;
            Margins margins = null;
            if (SemanticModel.FindEffective(m_source.Style, sc, 0, 0, IDENTIFIER.SIZINGMARGINS)?.GetValue() is Margins sm) margins = sm;

            uint edgePixel = plate.Pixels[0];
            int ix = margins != null && margins.Left > 0 ? Math.Min(margins.Left, plate.Width - 1) : plate.Width / 2;
            int iy = margins != null && margins.Top > 0 ? Math.Min(margins.Top, plate.Height - 1) : plate.Height / 2;
            uint facePixel = plate.Pixels[iy * plate.Width + ix];
            if ((edgePixel >> 24) != 0xFF || (facePixel >> 24) != 0xFF) return;
            var edge = Color.FromArgb((int)((edgePixel >> 16) & 0xFF), (int)((edgePixel >> 8) & 0xFF), (int)(edgePixel & 0xFF));
            var face = Color.FromArgb((int)((facePixel >> 16) & 0xFF), (int)((facePixel >> 8) & 0xFF), (int)(facePixel & 0xFF));

            var tnames = SemanticModel.PartNames(tc.ClassName, Platform.Win10);
            foreach (int partId in new[] { 0, 4, 5 })
            {
                if (!tc.Parts.TryGetValue(partId, out var part)) continue;
                string partName = tnames.TryGetValue(partId, out var n) ? n : "P" + partId;
                foreach (var st in part.States.Values)
                {
                    foreach (var (ident, color) in new[] { (IDENTIFIER.BORDERCOLOR, edge), (IDENTIFIER.FILLCOLOR, face) })
                    {
                        if (KeyPattern.Matches(m_opt.Exclude, tc.ClassName, partName, ident.ToString())) continue;
                        var prop = st.Properties.FirstOrDefault(p => p.Header.nameID == (int)ident && p.Header.typeID == (int)IDENTIFIER.COLOR);
                        if (prop != null && prop.GetValue() is Color same && same.R == color.R && same.G == color.G && same.B == color.B) continue;
                        if (prop == null)
                        {
                            prop = new StyleProperty(new PropertyHeader((int)ident, (int)IDENTIFIER.COLOR) { classID = tc.ClassId, partID = partId, stateID = st.StateId });
                            st.Properties.Add(prop);
                            AddedRecords.Add($"{tc.ClassId}/{partId}/{st.StateId}/{(int)ident}/{(int)IDENTIFIER.COLOR}");
                        }
                        prop.SetValue(color);
                        MarkTouched(prop, $"{tc.ClassName}|{partName}|S{st.StateId}|{ident}");
                        m_report.Decisions.Add(new MergeDecision
                        {
                            Class = tc.ClassName,
                            Part = partName,
                            State = st.StateId,
                            Property = ident.ToString(),
                            Action = "applied-xp-combo-colors",
                            Reason = "xp draws the combo frame from the Combobox common picture; win10 draws it as a border fill from these colors, so they were sampled off the source's picture instead of using the source's no-picture fallback colors",
                            Result = $"{color.R},{color.G},{color.B}",
                        });
                        m_report.Count("applied-xp-combo-colors");
                    }
                }
            }
        }

        /// <summary>
        /// xp only. ExplorerEx builds the classic start panel straight from the theme's StartPanel records:
        /// part 1 DEFAULTPANESIZE is the panel width, the other panes' DEFAULTPANESIZE must tile inside it,
        /// and part 6 (PLACELIST) CONTENTMARGINS place the places listview inside its pane (windows clip
        /// children to the parent, so a negative left moves the listview's contents under the clip).
        /// SevenVG declares panel 375, logoff 406x37, a pane starting at top -30 and places left -38: the
        /// logoff buttons ran 31px past the panel edge and "My Computer"/"Control Panel"/"Run..." lost their
        /// first letters. Templates derived from Luna fit (panel 380, user pane 190, logoff 190x37, places left
        /// 4), so only the misfit is trimmed.
        /// The places listview puts its item text 30px right of its origin and draws the leading shell icon in
        /// the 26px before it, so a negative offset between 0 and -28 leaves a cut-off icon and one below -28
        /// cuts the labels. -28 is the offset that hides the icon without touching the text, which is what the
        /// negative margin asks for: the theme paints a window-7 style sidebar with plain labels, and at 0 the
        /// 130px column wraps "My Computer".
        /// </summary>
        private void FitXpStartPanel()
        {
            if (!m_target.ClassesByName.TryGetValue("StartPanel", out var tc)) return;
            if (m_target.DuplicateClassNames.Contains("StartPanel")) return;

            (StyleProperty Prop, int State) Find(int partId, IDENTIFIER id)
            {
                if (!tc.Parts.TryGetValue(partId, out var part)) return (null, 0);
                foreach (var st in part.States.Values)
                    foreach (var p in st.Properties)
                        if (p.Header.nameID == (int)id) return (p, st.StateId);
                return (null, 0);
            }

            var (userPane, _) = Find(1, IDENTIFIER.DEFAULTPANESIZE);
            if (!(userPane?.GetValue() is Margins up) || up.Right <= 0) return;
            int panelWidth = up.Right;
            var tnames = SemanticModel.PartNames(tc.ClassName, Platform.Win10);

            for (int partId = 0; partId <= 8; partId++)
            {
                var (prop, stateId) = Find(partId, IDENTIFIER.DEFAULTPANESIZE);
                if (prop == null || !(prop.GetValue() is Margins m)) continue;
                bool width = m.Right > panelWidth, edge = m.Left < 0 || m.Top < 0;
                if (!width && !edge) continue;

                string partName = tnames.TryGetValue(partId, out var n) ? n : "P" + partId;
                m.Left = Math.Max(0, m.Left);
                m.Top = Math.Max(0, m.Top);
                m.Right = Math.Min(m.Right, panelWidth);
                prop.SetValue(m);
                MarkTouched(prop, $"{tc.ClassName}|{partName}|S{stateId}|DEFAULTPANESIZE");
                m_report.Decisions.Add(new MergeDecision
                {
                    Class = tc.ClassName,
                    Part = partName,
                    State = stateId,
                    Property = "DEFAULTPANESIZE",
                    Action = "fitted-xp-startpanel",
                    Reason = "xp's shell lays every start panel pane out inside the panel width; the source's pane would overflow or start outside its host, so it is clipped by the shell",
                    Result = $"{m.Left},{m.Top},{m.Right},{m.Bottom}",
                });
                m_report.Count("fitted-xp-startpanel");
            }

            var (places, placesState) = Find(6, IDENTIFIER.CONTENTMARGINS);
            if (places?.GetValue() is Margins pm && pm.Left < 0)
            {
                string partName = tnames.TryGetValue(6, out var n) ? n : "PLACELIST";
                pm.Left = -28;
                places.SetValue(pm);
                MarkTouched(places, $"{tc.ClassName}|{partName}|S{placesState}|CONTENTMARGINS");
                m_report.Decisions.Add(new MergeDecision
                {
                    Class = tc.ClassName,
                    Part = partName,
                    State = placesState,
                    Property = "CONTENTMARGINS",
                    Action = "fitted-xp-startpanel",
                    Reason = "the source hides the places icons by pulling the listview left; a smaller pull leaves a cut-off icon in front of every label and a larger one cuts the labels themselves, and -28 is the only offset that does neither",
                    Result = $"{pm.Left},{pm.Top},{pm.Right},{pm.Bottom}",
                });
                m_report.Count("fitted-xp-startpanel");
            }
        }

        private static string SizingTypeName(int value) =>
            value == 0 ? "TRUESIZE" : value == 1 ? "STRETCH" : value == 2 ? "TILE" : value.ToString();

        private static readonly IDENTIFIER[] ImageNames =
        {
            IDENTIFIER.IMAGEFILE, IDENTIFIER.IMAGEFILE1, IDENTIFIER.IMAGEFILE2, IDENTIFIER.IMAGEFILE3, IDENTIFIER.IMAGEFILE4,
            IDENTIFIER.IMAGEFILE5, IDENTIFIER.IMAGEFILE6, IDENTIFIER.IMAGEFILE7,
        };

        /// <summary>
        /// true when every picture the source uses in this part/state is also the picture the output draws
        /// there. Added pictures are read from the allocator (GetRawResource ignores queued data).
        /// </summary>
        private bool PicturesFromSource(StyleClass sc, StyleClass tc, int spid, int tpid, int stateId, Dictionary<int, ImageInfo> cache)
        {
            bool any = false, applied = false;
            foreach (var name in ImageNames)
            {
                if (!VisualStyle.XpImageIds.Contains((int)name)) continue; // xp has no IMAGEFILE6/7
                var sp = SemanticModel.FindEffective(m_source.Style, sc, spid, stateId, name);
                if (sp == null) continue;
                var tp = SemanticModel.FindEffective(m_target.Style, tc, tpid, stateId, name);
                if (tp == null) return false;
                if (m_touched.Contains(tp)) applied = true;
                var si = m_source.GetImageInfo("IMAGE", sp.Header.shortFlag & 0xFFFF);
                if (si == null || !si.Decodable) return false;
                int tid = tp.Header.shortFlag & 0xFFFF;
                if (!cache.TryGetValue(tid, out var ti))
                {
                    byte[] bytes = m_alloc.Added.TryGetValue("IMAGE/" + tid, out var added) ? added : m_target.GetResourceBytes("IMAGE", tid);
                    ti = bytes == null ? null : ImageIdentity.Analyze(bytes);
                    cache[tid] = ti;
                }
                if (ti == null || !ti.Decodable || si.PixelSha1 != ti.PixelSha1) return false;
                any = true;
            }
            // only a picture the import actually replaced: the template's own picture (stock Luna onto the Luna
            // port) was left with the SIZINGTYPE its author chose, so its deliberate adaptation stays.
            return any && applied;
        }

        /// <summary>true when the part/state's effective sizing margins are the source's, not the template's.</summary>
        private bool MarginsFromSource(StyleClass sc, StyleClass tc, int spid, int tpid, int stateId)
        {
            var sm = SemanticModel.FindEffective(m_source.Style, sc, spid, stateId, IDENTIFIER.SIZINGMARGINS);
            if (sm == null) return true;
            var tm = SemanticModel.FindEffective(m_target.Style, tc, tpid, stateId, IDENTIFIER.SIZINGMARGINS);
            return tm != null && sm.GetValue() is Margins a && tm.GetValue() is Margins b && a.Equals(b);
        }

        /// <summary>
        /// xp only. xp's uxtheme blends a picture with true alpha (some pixel partly transparent) by its alpha channel
        /// whatever TRANSPARENT says: fAlphaChannel comes from the bitmap header (imagefile.cpp SetImageInfo,
        /// pdi->fAlphaChannel = fTrueAlpha) and TRANSPARENT only switches the color key. Windows 10 doesn't blend a part
        /// with TRANSPARENT false: XPMC's toolbar buttons (TRANSPARENT = False on a 32bpp alpha strip) were drawn on
        /// black. Every state that now shows a picture with partial alpha and says TRANSPARENT false gets true,
        /// which is how xp drew it. Template pictures stay as they are (Luna10's frames are opaque).
        /// </summary>
        private void TransparentForTrueAlpha()
        {
            foreach (var cls in m_target.Style.Classes.Values)
                foreach (var part in cls.Parts.Values)
                    foreach (var st in part.States.Values)
                    {
                        var tr = st.Properties.FirstOrDefault(p => p.Header.nameID == (int)IDENTIFIER.TRANSPARENT_ && p.Header.typeID == (int)IDENTIFIER.BOOLTYPE);
                        if (tr == null || !(tr.GetValue() is bool on) || on) continue;
                        if (KeyPattern.Matches(m_opt.Exclude, cls.ClassName, part.PartName, "TRANSPARENT")) continue;
                        bool alpha = st.Properties.Where(p => p.Header.nameID == (int)IDENTIFIER.IMAGEFILE || DpiSlotIds.Contains(p.Header.nameID))
                            .Select(p => p.GetValue() is int id && m_alloc.Added.TryGetValue("IMAGE/" + id, out var bytes) ? bytes : null)
                            .Where(b => b != null)
                            .Any(b => ImageIdentity.Decode(b).Pixels.Any(px => (px >> 24) != 0 && (px >> 24) != 0xFF));
                        if (!alpha) continue;
                        tr.SetValue(true);
                        MarkTouched(tr, $"{cls.ClassName}|{part.PartName}|S{st.StateId}|TRANSPARENT");
                        m_report.Decisions.Add(new MergeDecision
                        {
                            Class = cls.ClassName, Part = part.PartName, State = st.StateId, Property = "TRANSPARENT", Action = "applied-true-alpha",
                            Reason = "the picture has partial alpha, which xp blended whatever TRANSPARENT said; windows 10 only blends with TRANSPARENT true",
                            Result = "true",
                        });
                        m_report.Count("applied-true-alpha");
                    }
        }

        /// <summary>
        /// xp only. comctl32's up-down erases with its class brush (BTNFACE, updown.c) before drawing the Spin parts,
        /// so a see-through button picture showed BTNFACE on xp. WinForms' NumericUpDown paints the parts on an
        /// unerased opaque control: GSM's fully transparent button backgrounds came out white. A source picture with
        /// transparent pixels is flattened onto the source's BTNFACE, which looks the same in comctl32.
        /// </summary>
        private void OpaqueSpinBackgrounds()
        {
            if (!m_target.ClassesByName.TryGetValue("Spin", out var tc) || m_target.DuplicateClassNames.Contains("Spin")
                || KeyPattern.Matches(m_opt.Exclude, "Spin", "*", "IMAGEFILE")) return;
            if (!m_source.ClassesByName.TryGetValue("sysmetrics", out var sm)
                || !(SemanticModel.FindEffective(m_source.Style, sm, 0, 0, IDENTIFIER.BTNFACE)?.GetValue() is Color face)) return;
            var flattened = new Dictionary<int, int>();
            for (int part = 1; part <= 4; ++part)
            {
                if (!tc.Parts.TryGetValue(part, out var p)) continue;
                foreach (var st in p.States.Values)
                {
                    var img = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)IDENTIFIER.IMAGEFILE);
                    if (img == null || !(img.GetValue() is int id)) continue;
                    if (!flattened.TryGetValue(id, out int newId))
                    {
                        // only pictures this run brought in: the template's own are win10 art drawn for win10.
                        if (!m_alloc.Added.TryGetValue("IMAGE/" + id, out var bytes)) continue;
                        ArgbImage pic;
                        try { pic = ImageIdentity.Decode(bytes); } catch (ArgumentException) { continue; }
                        if (pic.Pixels.All(px => (px >> 24) == 0xFF)) continue;
                        var flat = new ArgbImage(pic.Width, pic.Height);
                        for (int i = 0; i < pic.Pixels.Length; ++i)
                        {
                            uint px = pic.Pixels[i], a = px >> 24;
                            uint Mix(int shift, int under) => (uint)((((px >> shift) & 0xFF) * a + (uint)under * (255 - a) + 127) / 255);
                            flat.Pixels[i] = 0xFF000000 | (Mix(16, face.R) << 16) | (Mix(8, face.G) << 8) | Mix(0, face.B);
                        }
                        var png = ImageIdentity.EncodePngVerified(flat);
                        newId = PlaceResource("IMAGE", id, png, ImageIdentity.Analyze(png));
                        flattened[id] = newId;
                        m_report.Notes.Add($"Spin IMAGE {id}: see-through button picture flattened onto the source's BTNFACE {face.R},{face.G},{face.B} as IMAGE {newId} (WinForms' NumericUpDown doesn't erase under it).");
                        m_report.Count("applied-opaque-spin");
                    }
                    img.SetValue(newId);
                    MarkTouched(img, $"Spin|{p.PartName}|S{st.StateId}|IMAGEFILE");
                }
            }
        }

        // win10 menu records Luna10 filled with stock xp's system colors, and the xp color each stands for. XP drew
        // menus unthemed (flat menus: user32 mndraw.c MENU fill, MENUHILIGHT hot fill with a HIGHLIGHT frame and
        // HIGHLIGHTTEXT text, BTNSHADOW border; shell32 menuband mnbase.cpp the same for menu bands such as the
        // Quick Launch chevron menu). Windows 10 draws them themed from these records, so they kept Luna's blue
        // highlight and white popups under every other theme.
        private static readonly (string Class, int Part, int State, IDENTIFIER Prop, IDENTIFIER From)[] XpMenuColors =
        {
            ("Menu", 7, 0, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENUBAR), ("Menu", 7, 0, IDENTIFIER.BORDERCOLOR, IDENTIFIER.MENUBAR),
            ("Menu", 8, 0, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENUBAR), ("Menu", 8, 0, IDENTIFIER.TEXTCOLOR, IDENTIFIER.MENUTEXT),
            ("Menu", 8, 2, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENUHILIGHT), ("Menu", 8, 2, IDENTIFIER.TEXTCOLOR, IDENTIFIER.HIGHLIGHTTEXT),
            ("Menu", 8, 3, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENUHILIGHT), ("Menu", 8, 3, IDENTIFIER.TEXTCOLOR, IDENTIFIER.HIGHLIGHTTEXT),
            ("Menu", 8, 4, IDENTIFIER.TEXTCOLOR, IDENTIFIER.GRAYTEXT), ("Menu", 8, 5, IDENTIFIER.TEXTCOLOR, IDENTIFIER.GRAYTEXT),
            ("Menu", 8, 6, IDENTIFIER.TEXTCOLOR, IDENTIFIER.GRAYTEXT),
            ("Menu", 14, 0, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENU), ("Menu", 14, 0, IDENTIFIER.TEXTCOLOR, IDENTIFIER.MENUTEXT),
            ("Menu", 14, 1, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENU), ("Menu", 14, 1, IDENTIFIER.TEXTCOLOR, IDENTIFIER.MENUTEXT),
            ("Menu", 14, 2, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENUHILIGHT), ("Menu", 14, 2, IDENTIFIER.TEXTCOLOR, IDENTIFIER.HIGHLIGHTTEXT),
            ("Menu", 14, 3, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENU), ("Menu", 14, 3, IDENTIFIER.TEXTCOLOR, IDENTIFIER.GRAYTEXT),
            ("Menu", 14, 4, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENUHILIGHT), ("Menu", 14, 4, IDENTIFIER.TEXTCOLOR, IDENTIFIER.GRAYTEXT),
            // explorer's menu bands (the toolbar chevron menu, Favorites): a menu drawn as a toolbar.
            ("ExplorerMenu::Toolbar", 0, 0, IDENTIFIER.TEXTCOLOR, IDENTIFIER.MENUTEXT),
            ("ExplorerMenu::Toolbar", 0, 2, IDENTIFIER.TEXTCOLOR, IDENTIFIER.HIGHLIGHTTEXT),
            ("ExplorerMenu::Toolbar", 0, 3, IDENTIFIER.TEXTCOLOR, IDENTIFIER.HIGHLIGHTTEXT),
            ("ExplorerMenu::Toolbar", 0, 4, IDENTIFIER.TEXTCOLOR, IDENTIFIER.GRAYTEXT),
            ("ExplorerMenu::Toolbar", 1, 0, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENU),
            ("ExplorerMenu::Toolbar", 1, 2, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENUHILIGHT),
            ("ExplorerMenu::Toolbar", 1, 3, IDENTIFIER.FILLCOLOR, IDENTIFIER.MENUHILIGHT),
            ("StartMenu::Toolbar", 0, 0, IDENTIFIER.TEXTCOLOR, IDENTIFIER.MENUTEXT),
            ("StartMenu::Toolbar", 0, 2, IDENTIFIER.TEXTCOLOR, IDENTIFIER.HIGHLIGHTTEXT),
            ("StartMenu::MenuBand", 0, 0, IDENTIFIER.TEXTCOLOR, IDENTIFIER.MENUTEXT),
            ("StartMenu::MenuBand", 1, 0, IDENTIFIER.TEXTCOLOR, IDENTIFIER.MENUTEXT),
        };

        // the plain Luna10 menu pictures: every pixel is one of stock xp's menu colors (or see-through), so each is
        // repainted pixel by pixel in the source's colors. Pictures with other art (check marks, arrows) stay.
        private static readonly (string Class, int Part, IDENTIFIER Prop)[] XpMenuPictures =
        {
            ("Menu", 9, IDENTIFIER.IMAGEFILE),   // POPUPBACKGROUND: MENU
            ("Menu", 10, IDENTIFIER.IMAGEFILE),  // POPUPBORDERS: BTNSHADOW frame around MENU
            ("Menu", 13, IDENTIFIER.IMAGEFILE),  // POPUPGUTTER: MENU
            ("Menu", 14, IDENTIFIER.IMAGEFILE),  // POPUPITEM: see-through / MENUHILIGHT frames
            ("Menu", 15, IDENTIFIER.IMAGEFILE),  // POPUPSEPARATOR: BTNSHADOW line over MENU
            ("ExplorerMenu::Toolbar", 0, IDENTIFIER.IMAGEFILE), // menu band background: MENU
        };

        /// <summary>
        /// xp only: the menu records and plain menu pictures follow the source's SysMetrics menu colors
        /// (<see cref="XpMenuColors"/>, <see cref="XpMenuPictures"/>). Untouched when the source keeps stock xp's
        /// colors. A stock color a picture uses for two roles (stock MENU and HIGHLIGHTTEXT are both white) is
        /// mapped by the picture's role, so a white popup background becomes the source's MENU, not its text color.
        /// </summary>
        private void MenuColorsFromXp()
        {
            if (!m_source.ClassesByName.TryGetValue("sysmetrics", out var sm) || !m_base.ClassesByName.TryGetValue("sysmetrics", out var bm)) return;
            Color? Src(IDENTIFIER id) => SemanticModel.FindEffective(m_source.Style, sm, 0, 0, id)?.GetValue() as Color?;
            Color? Stock(IDENTIFIER id) => SemanticModel.FindEffective(m_base.Style, bm, 0, 0, id)?.GetValue() as Color?;
            // XP's user32 fills a missing MENUHILIGHT/MENUBAR from HIGHLIGHT/BTNFACE (flat menu defaults).
            Color? Role(IDENTIFIER id) => Src(id) ?? (id == IDENTIFIER.MENUHILIGHT ? Src(IDENTIFIER.HIGHLIGHT) : id == IDENTIFIER.MENUBAR ? Src(IDENTIFIER.BTNFACE) : null);
            var roles = new[] { IDENTIFIER.MENU, IDENTIFIER.MENUTEXT, IDENTIFIER.MENUBAR, IDENTIFIER.MENUHILIGHT, IDENTIFIER.HIGHLIGHT,
                IDENTIFIER.HIGHLIGHTTEXT, IDENTIFIER.GRAYTEXT, IDENTIFIER.BTNSHADOW };
            if (roles.All(r => Role(r) is Color s && Stock(r) is Color b && s.ToArgb() == b.ToArgb())) return;

            int changed = 0;
            foreach (var m in XpMenuColors)
            {
                if (!m_target.ClassesByName.TryGetValue(m.Class, out var tc) || m_target.DuplicateClassNames.Contains(m.Class)
                    || KeyPattern.Matches(m_opt.Exclude, m.Class, "*", m.Prop.ToString())) continue;
                if (!(Role(m.From) is Color want)) continue;
                // a role the source keeps at stock xp's color leaves the template's value, adaptations included
                // (Luna10 draws disabled bar text 109,109,109 where xp's GRAYTEXT is 172,168,153).
                if (Stock(m.From) is Color stockRole && stockRole.ToArgb() == want.ToArgb()) continue;
                if (!tc.Parts.TryGetValue(m.Part, out var part) || !part.States.TryGetValue(m.State, out var st)) continue;
                var p = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)m.Prop && x.Header.typeID == (int)IDENTIFIER.COLOR);
                if (p == null || m_touched.Contains(p) || !(p.GetValue() is Color have) || have.ToArgb() == want.ToArgb()) continue;
                p.SetValue(want);
                string label = $"{m.Class}|{part.PartName}|S{m.State}|{SemanticModel.PropertyName((int)m.Prop)}";
                MarkTouched(p, label);
                m_report.Decisions.Add(new MergeDecision
                {
                    Class = m.Class, Part = part.PartName, State = m.State, Property = SemanticModel.PropertyName((int)m.Prop),
                    Action = "applied-menu-color", Reason = $"xp drew this menu part in the {m.From} system color",
                    Result = SemanticModel.FormatValue(p),
                });
                ++changed;
            }

            // stock color -> source color, per picture role. Unknown pixels leave the picture alone.
            Color Op(IDENTIFIER id) => Role(id) ?? Stock(id).Value;
            var byRole = new Dictionary<int, Dictionary<int, Color>>
            {
                [9] = new Dictionary<int, Color> { [Stock(IDENTIFIER.MENU).Value.ToArgb()] = Op(IDENTIFIER.MENU) },
                [10] = new Dictionary<int, Color> { [Stock(IDENTIFIER.MENU).Value.ToArgb()] = Op(IDENTIFIER.MENU), [Stock(IDENTIFIER.BTNSHADOW).Value.ToArgb()] = Op(IDENTIFIER.BTNSHADOW) },
                [13] = new Dictionary<int, Color> { [Stock(IDENTIFIER.MENU).Value.ToArgb()] = Op(IDENTIFIER.MENU) },
                [14] = new Dictionary<int, Color> { [Stock(IDENTIFIER.MENUHILIGHT).Value.ToArgb()] = Op(IDENTIFIER.MENUHILIGHT) },
                [15] = new Dictionary<int, Color> { [Stock(IDENTIFIER.MENU).Value.ToArgb()] = Op(IDENTIFIER.MENU), [Stock(IDENTIFIER.BTNSHADOW).Value.ToArgb()] = Op(IDENTIFIER.BTNSHADOW) },
                [0] = new Dictionary<int, Color> { [Stock(IDENTIFIER.MENU).Value.ToArgb()] = Op(IDENTIFIER.MENU) },
            };
            var redone = new Dictionary<(int, int), int>();
            foreach (var pic in XpMenuPictures)
            {
                if (!m_target.ClassesByName.TryGetValue(pic.Class, out var tc) || m_target.DuplicateClassNames.Contains(pic.Class)
                    || KeyPattern.Matches(m_opt.Exclude, pic.Class, "*", "IMAGEFILE")) continue;
                if (!tc.Parts.TryGetValue(pic.Part, out var part) || !part.States.TryGetValue(0, out var st)) continue;
                var img = st.Properties.FirstOrDefault(x => x.Header.nameID == (int)pic.Prop);
                if (img == null || m_touched.Contains(img) || !(img.GetValue() is int id)) continue;
                var map = byRole[pic.Part];
                if (!redone.TryGetValue((pic.Part, id), out int newId))
                {
                    var bytes = m_target.Style.GetRawResource("IMAGE", id);
                    if (bytes == null) continue;
                    ArgbImage src;
                    try { src = ImageIdentity.Decode(bytes); } catch (ArgumentException) { continue; }
                    var outImg = new ArgbImage(src.Width, src.Height);
                    bool known = true, any = false;
                    for (int i = 0; i < src.Pixels.Length && known; ++i)
                    {
                        uint px = src.Pixels[i];
                        if ((px >> 24) == 0) { outImg.Pixels[i] = px; continue; }
                        if ((px >> 24) != 0xFF || !map.TryGetValue((int)(px | 0xFF000000), out var to)) { known = false; break; }
                        outImg.Pixels[i] = 0xFF000000 | ((uint)to.R << 16) | ((uint)to.G << 8) | to.B;
                        any |= outImg.Pixels[i] != px;
                    }
                    if (!known || !any) continue;
                    var png = ImageIdentity.EncodePngVerified(outImg);
                    newId = PlaceResource("IMAGE", id, png, ImageIdentity.Analyze(png));
                    redone[(pic.Part, id)] = newId;
                }
                img.SetValue(newId);
                MarkTouched(img, $"{pic.Class}|{part.PartName}|S0|IMAGEFILE");
                m_report.Decisions.Add(new MergeDecision
                {
                    Class = pic.Class, Part = part.PartName, State = 0, Property = "IMAGEFILE", Action = "applied-menu-color",
                    Reason = "the template's plain menu picture repainted in the source's menu system colors", Result = newId.ToString(),
                });
                ++changed;
            }
            if (changed > 0) m_report.Count("applied-menu-color", changed);
        }

        // controls whose WS_EX_CLIENTEDGE comctl32 paints from the theme (CCDrawNonClientTheme callers).
        private static readonly string[] ClientEdgeClasses = { "ListView", "TreeView", "ListBox" };

        /// <summary>
        /// xp only. comctl32 (cutils.c CCDrawNonClientTheme) draws a client edge with the part's background, then
        /// fills what a BORDERSIZE thinner than the 2px edge leaves with a brush that is white on win10 for Task
        /// Manager's and the Open dialog's lists: a white ring inside GSM's dark border. SIZINGBORDERWIDTH at the
        /// edge width skips that fill, so the BORDERFILL (border, then its FILLCOLOR) covers the whole edge.
        /// Only when the fill isn't white already (Luna's lists look the same either way).
        /// </summary>
        private void ThemeWholeClientEdge()
        {
            foreach (var name in ClientEdgeClasses)
            {
                if (!m_target.ClassesByName.TryGetValue(name, out var tc) || m_target.DuplicateClassNames.Contains(name)
                    || KeyPattern.Matches(m_opt.Exclude, name, "*", "SIZINGBORDERWIDTH")) continue;
                if (!tc.Parts.TryGetValue(0, out var part) || !part.States.TryGetValue(0, out var state)) continue;
                if (!(SemanticModel.FindEffective(m_target.Style, tc, 0, 0, IDENTIFIER.BGTYPE)?.GetValue() is int bg) || bg != 1) continue;
                if (!(SemanticModel.FindEffective(m_target.Style, tc, 0, 0, IDENTIFIER.FILLCOLOR)?.GetValue() is Color fill)
                    || (fill.R == 255 && fill.G == 255 && fill.B == 255)) continue;
                if (SemanticModel.FindEffective(m_target.Style, tc, 0, 0, IDENTIFIER.SIZINGBORDERWIDTH) != null) continue;
                if (SemanticModel.FindEffective(m_target.Style, tc, 0, 0, IDENTIFIER.BORDERSIZE)?.GetValue() is int bs && bs >= 2) continue;

                var p = new StyleProperty(new PropertyHeader((int)IDENTIFIER.SIZINGBORDERWIDTH, (int)IDENTIFIER.INT) { classID = tc.ClassId, partID = 0, stateID = 0 });
                p.SetValue(2);
                state.Properties.Add(p);
                AddedRecords.Add($"{tc.ClassId}/0/0/{(int)IDENTIFIER.SIZINGBORDERWIDTH}/{(int)IDENTIFIER.INT}");
                MarkTouched(p, $"{name}|{part.PartName}|S0|SIZINGBORDERWIDTH");
                m_report.Notes.Add($"{name}: SIZINGBORDERWIDTH 2 added so the theme paints the whole client edge in {fill.R},{fill.G},{fill.B} (comctl32 filled the rest white).");
                m_report.Count("applied-client-edge");
            }
        }

        // win10 class/part that draws what an xp class/part drew, and the xp class/part whose picture it takes.
        // Explorer::TreeView doesn't inherit TreeView; its HOTGLYPH (no xp counterpart) shows the same +/- as GLYPH.
        private static readonly (string Class, string Part, string FromClass, string FromPart)[] XpImageHeirs =
        {
            ("Explorer::TreeView", "GLYPH", "TreeView", "GLYPH"),
            ("Explorer::TreeView", "HOTGLYPH", "TreeView", "GLYPH"),
        };

        /// <summary>
        /// xp only. A source state with one IMAGEFILE where the template picks its picture from IMAGEFILE1-7
        /// (IMAGESELECTTYPE set): uxtheme then never reads IMAGEFILE, so GSM's tree +/- glyphs showed Luna10's.
        /// Every template dpi slot of that state gets the source's picture, in the part itself and in the
        /// win10 parts that replaced it (<see cref="XpImageHeirs"/>). Only when the source changed the picture.
        /// </summary>
        private void SingleImageIntoDpiSlots()
        {
            foreach (var s in m_source.Entries.Where(e => e.ResourceType == "IMAGE" && e.NameId == (int)IDENTIFIER.IMAGEFILE).ToList())
            {
                if (m_source.Entries.Any(o => o.StateKey == s.StateKey && DpiSlotIds.Contains(o.NameId))) continue;
                if (m_base.ByKey.TryGetValue(s.Key, out var b) && Same(m_base, b, m_source, s)) continue;
                var targets = new[] { (s.ClassName, s.PartName) }.Concat(XpImageHeirs
                    .Where(h => string.Equals(h.FromClass, s.ClassName, StringComparison.OrdinalIgnoreCase) && h.FromPart == s.PartName)
                    .Select(h => (h.Class, h.Part)));
                foreach (var (cls, partName) in targets)
                {
                    if (m_target.DuplicateClassNames.Contains(cls) || KeyPattern.Matches(m_opt.Exclude, cls, partName, "IMAGEFILE*")) continue;
                    var slots = m_target.Entries.Where(t => t.ClassName == cls && t.PartName == partName && t.StateId == s.StateId
                        && t.ResourceType == "IMAGE" && DpiSlotIds.Contains(t.NameId) && !m_touched.Contains(t.Property)).ToList();
                    if (slots.Count == 0) continue;
                    var tcls = m_target.Style.Classes[slots[0].Property.Header.classID];
                    if (SemanticModel.FindEffective(m_target.Style, tcls, slots[0].PartId, s.StateId, IDENTIFIER.IMAGESELECTTYPE)?.GetValue() is int ist && ist == 0) continue;
                    // an image glyph with IMAGESELECTTYPE picks its glyph from IMAGEFILE1-7 (Luna10 Toolbar SPLITBUTTONDROPDOWN,
                    // the back/forward dropdown arrows): those slots are glyph sizes, which FillDpiSlots gives the source's
                    // GLYPHIMAGEFILE. The button background there left ElementA's arrows blank.
                    if (SemanticModel.FindEffective(m_target.Style, tcls, slots[0].PartId, s.StateId, IDENTIFIER.GLYPHTYPE)?.GetValue() is int gt && gt == 1) continue;
                    foreach (var t in slots.Where(t => !Same(m_source, s, m_target, t)))
                        ApplyExtra(t, s, b, "applied-single-image-slot",
                            $"xp sets one IMAGEFILE; the template picks {t.PropertyName} by dpi, so it gets the source's picture"
                            + (cls == s.ClassName && partName == s.PartName ? "" : $" ({cls} {partName} draws what xp's {s.ClassName} {s.PartName} drew)"));
                }
            }
        }

        /// <summary>
        /// xp only. An image glyph picked by size (GLYPHTYPE IMAGEGLYPH, IMAGESELECTTYPE SIZE) is IMAGEFILE1-5 with
        /// MINSIZE1-5: uxtheme draws the largest one whose min size fits the part (imagefile.cpp SelectCorrectImageFile),
        /// a missing MINSIZE being the glyph's own frame size (SetImageInfo: szMinSize = iSingleWidth x iSingleHeight).
        /// The template's slots have their own min sizes (Luna10 ScrollBar ARROWBTN: 17/21/26 where xp's were 7/9; its
        /// caption buttons none, so their redrawn glyphs' frame sizes), so a slot copied one to one shows a different
        /// glyph than xp did at that size: Ana Muslims Vista's empty 7px arrow, Luna10's white caption glyphs on
        /// XPMC's small buttons. Each template slot gets the source glyph xp picks at that slot's min size.
        /// </summary>
        private void GlyphSizeSlotsFromXp()
        {
            // MINSIZE1-5 go with IMAGEFILE1-5; win10's IMAGEFILE6/7 have no min size and are only reached past MINSIZE5.
            var minSizeIds = new[] { IDENTIFIER.MINSIZE1, IDENTIFIER.MINSIZE2, IDENTIFIER.MINSIZE3, IDENTIFIER.MINSIZE4, IDENTIFIER.MINSIZE5 };
            var slotIds = new[] { IDENTIFIER.IMAGEFILE1, IDENTIFIER.IMAGEFILE2, IDENTIFIER.IMAGEFILE3, IDENTIFIER.IMAGEFILE4, IDENTIFIER.IMAGEFILE5 };
            // the min size uxtheme uses for one slot: MINSIZEn, or the picture's frame (IMAGECOUNT frames, IMAGELAYOUT).
            Size? MinSize(SemanticModel m, StyleClass c, int part, int stateId, int slot, SemanticEntry e)
            {
                if (slot >= 0 && slot < minSizeIds.Length && SemanticModel.FindEffective(m.Style, c, part, stateId, minSizeIds[slot])?.GetValue() is Size s) return s;
                var img = Decoded(m, "IMAGE", e.ResourceId);
                if (img == null) return null;
                int n = SemanticModel.FindEffective(m.Style, c, part, stateId, IDENTIFIER.IMAGECOUNT)?.GetValue() is int cnt && cnt > 0 ? cnt : 1;
                bool horizontal = SemanticModel.FindEffective(m.Style, c, part, stateId, IDENTIFIER.IMAGELAYOUT)?.GetValue() is int lo && lo == 1;
                return horizontal ? new Size(img.Width / n, img.Height) : new Size(img.Width, img.Height / n);
            }
            foreach (var state in m_source.Entries.Where(e => e.ResourceType == "IMAGE" && e.NameId == (int)IDENTIFIER.IMAGEFILE1)
                .Select(e => (e.ClassName, e.PartName, e.StateId, e.StateKey)).Distinct().ToList())
            {
                if (m_target.DuplicateClassNames.Contains(state.ClassName) || KeyPattern.Matches(m_opt.Exclude, state.ClassName, state.PartName, "IMAGEFILE*")) continue;
                var src = m_source.Entries.Where(e => e.StateKey == state.StateKey).ToList();
                var sp = src.FirstOrDefault()?.Property;
                if (sp == null || !m_source.Style.Classes.TryGetValue(sp.Header.classID, out var scls)) continue;
                if (!(SemanticModel.FindEffective(m_source.Style, scls, sp.Header.partID, state.StateId, IDENTIFIER.GLYPHTYPE)?.GetValue() is int sg && sg == 1)) continue;
                if (!(SemanticModel.FindEffective(m_source.Style, scls, sp.Header.partID, state.StateId, IDENTIFIER.IMAGESELECTTYPE)?.GetValue() is int ss && ss == 1)) continue;
                // the source's glyph files in slot order with the min size xp gave each.
                var glyphs = new List<(SemanticEntry Entry, int W, int H)>();
                for (int i = 0; i < slotIds.Length; ++i)
                {
                    var e = src.FirstOrDefault(x => x.NameId == (int)slotIds[i] && x.ResourceType == "IMAGE");
                    if (e == null) break;
                    if (!(MinSize(m_source, scls, sp.Header.partID, state.StateId, i, e) is Size sz)) { glyphs.Clear(); break; }
                    glyphs.Add((e, sz.Width, sz.Height));
                }
                if (glyphs.Count < 1) continue;
                // glyphs the source kept at stock xp's stay the template's (Luna10 redrew them for its larger slots).
                if (glyphs.All(g => m_base.ByKey.TryGetValue(g.Entry.Key, out var b) && Same(m_base, b, m_source, g.Entry))) continue;

                var slots = m_target.Entries.Where(t => t.ClassName == state.ClassName && t.PartName == state.PartName && t.StateId == state.StateId
                    && t.ResourceType == "IMAGE" && DpiSlotIds.Contains(t.NameId)).ToList();
                if (slots.Count == 0) continue;
                var tcls = m_target.Style.Classes[slots[0].Property.Header.classID];
                if (!(SemanticModel.FindEffective(m_target.Style, tcls, slots[0].PartId, state.StateId, IDENTIFIER.GLYPHTYPE)?.GetValue() is int tg && tg == 1)) continue;
                if (!(SemanticModel.FindEffective(m_target.Style, tcls, slots[0].PartId, state.StateId, IDENTIFIER.IMAGESELECTTYPE)?.GetValue() is int ts && ts == 1)) continue;
                // min sizes of the template's own pictures, read before any slot is rewritten.
                var targetMin = slots.ToDictionary(t => t, t => MinSize(m_target, tcls, t.PartId, state.StateId, Array.IndexOf(slotIds, (IDENTIFIER)t.NameId), t));
                // a source without MINSIZE (min size from the frame) only fills slots the merge left at the template's
                // pictures: XPMC sets IMAGEFILE1 alone and Luna10's own glyphs stayed in 2-7. Slots the merge already gave
                // the source's pictures (TangoLux's caption buttons, one per size) are kept as they are.
                bool explicitMin = Enumerable.Range(0, glyphs.Count).All(i =>
                    SemanticModel.FindEffective(m_source.Style, scls, sp.Header.partID, state.StateId, minSizeIds[i])?.GetValue() is Size);
                foreach (var t in slots)
                {
                    if (!explicitMin && m_touched.Contains(t.Property)) continue;
                    var tms = targetMin[t];
                    var pick = tms is Size tsz
                        ? glyphs.LastOrDefault(g => g.W <= tsz.Width && g.H <= tsz.Height)
                        : glyphs[glyphs.Count - 1];
                    if (pick.Entry == null) pick = glyphs[0]; // smaller than every source glyph: xp drew the first.
                    if (Same(m_source, pick.Entry, m_target, t)) continue;
                    ApplyExtra(t, pick.Entry, null, "applied-glyph-size-slot",
                        $"xp picks the image glyph by size; at this slot's min size ({(tms is Size z ? $"{z.Width}x{z.Height}" : "unknown")}) it drew the source's {pick.Entry.PropertyName} ({pick.W}x{pick.H})");
                }
            }
        }

        // win10 parts the template draws with a light picture, and the xp color that filled the same area on xp.
        private static readonly (string Class, int Part, string FromClass, int FromPart, IDENTIFIER FromProp, string Why)[] XpFlatFills =
        {
            // xp has no Edit BACKGROUND: a text box's inside is the EDITTEXT fill. win10's themed edit paints
            // EP_BACKGROUND first, and Luna10's white picture made GSM's dark address bar and text boxes white.
            ("Edit", 3, "Edit", 1, IDENTIFIER.FILLCOLOR, "win10 paints Edit BACKGROUND under the text; xp's EDITTEXT fill"),
            // explorer's pane splitters (shellstyle dtb(PreviewPane, 3/4, 0)): Luna10 has a beige 3px picture.
            // xp's folders pane splitter was a plain BTNFACE bar.
            ("PreviewPane", 3, "sysmetrics", 0, IDENTIFIER.BTNFACE, "the nav pane splitter was xp's BTNFACE"),
            ("PreviewPane", 4, "sysmetrics", 0, IDENTIFIER.BTNFACE, "the reading pane splitter was xp's BTNFACE"),
        };

        /// <summary>
        /// xp only: <see cref="XpFlatFills"/>. When the source's color differs from stock, the template picture
        /// (state 0, BGTYPE imagefile) becomes a borderless fill in that color.
        /// </summary>
        private void FillFlatFromXp()
        {
            foreach (var f in XpFlatFills)
            {
                if (!m_target.ClassesByName.TryGetValue(f.Class, out var tc) || m_target.DuplicateClassNames.Contains(f.Class)
                    || !m_source.ClassesByName.TryGetValue(f.FromClass, out var sc) || !m_base.ClassesByName.TryGetValue(f.FromClass, out var bc)
                    || KeyPattern.Matches(m_opt.Exclude, f.Class, "*", "FILLCOLOR")) continue;
                var sp = SemanticModel.FindEffective(m_source.Style, sc, f.FromPart, 0, f.FromProp);
                var bp = SemanticModel.FindEffective(m_base.Style, bc, f.FromPart, 0, f.FromProp);
                if (!(sp?.GetValue() is Color fill) || (bp?.GetValue() is Color stock && stock.ToArgb() == fill.ToArgb())) continue;
                if (!tc.Parts.TryGetValue(f.Part, out var part) || !part.States.TryGetValue(0, out var state)) continue;
                var bg = state.Properties.FirstOrDefault(p => p.Header.nameID == (int)IDENTIFIER.BGTYPE);
                if (bg == null || !(bg.GetValue() is int t) || t != 0 || m_touched.Contains(bg)) continue; // only an untouched imagefile background

                string label = $"{f.Class}|{part.PartName}|S0|";
                bg.SetValue(1); // BORDERFILL
                MarkTouched(bg, label + "BGTYPE");
                foreach (var (id, type, value) in new (IDENTIFIER, IDENTIFIER, object)[] { (IDENTIFIER.BORDERSIZE, IDENTIFIER.INT, 0), (IDENTIFIER.FILLCOLOR, IDENTIFIER.COLOR, fill) })
                {
                    var p = state.Properties.FirstOrDefault(x => x.Header.nameID == (int)id);
                    if (p == null)
                    {
                        p = new StyleProperty(new PropertyHeader((int)id, (int)type) { classID = tc.ClassId, partID = f.Part, stateID = 0 });
                        state.Properties.Add(p);
                        AddedRecords.Add($"{tc.ClassId}/{f.Part}/0/{(int)id}/{(int)type}");
                    }
                    if (value is Color c) p.SetValue(c); else p.SetValue((int)value);
                    MarkTouched(p, label + id);
                }
                var s = m_source.Entries.First(e => ReferenceEquals(e.Property, sp));
                Decide(s, "applied-flat-fill", $"{f.Why} replaces the template's picture on {f.Class} part {f.Part}",
                    null, s, null, $"BORDERFILL {SemanticModel.FormatValue(sp)}");
                m_report.Count("applied-flat-fill");
            }
        }

        // confidence belongs to the role mapping, not just to the destination property's consumer.
        // SIZEBOXBKGND's consumer is verified: comctl32 19041.7663 DrawSizeBoxEx reads 11/0/FILLCOLOR.
        // its mapping to win7's BTNFACE is inferred; both stock values are 240,240,240.
        private static readonly (string Class, int Part, int State, IDENTIFIER Prop, string FromClass, int FromPart, int FromState, IDENTIFIER FromProp, bool Verified)[] Win10Mirrors =
        {
            // win7's nav pane is ProperTree's FILLCOLORHINT (shellstyle UIFILE), not the tree's own fill;
            // a style can set the two differently and win7 shows the hint.
            ("ExplorerNavPane", 0, 0, IDENTIFIER.FILLCOLOR, "ProperTree", 2, 0, IDENTIFIER.FILLCOLORHINT, true),
            // xp's folders pane is a plain TreeView. win10 draws it with ExplorerNavPane and Explorer::TreeView,
            // win10-only classes that don't inherit TreeView, so GSM's dark tree pane stayed white.
            ("ExplorerNavPane", 0, 0, IDENTIFIER.FILLCOLOR, "TreeView", 0, 0, IDENTIFIER.FILLCOLOR, false),
            ("Explorer::TreeView", 0, 0, IDENTIFIER.FILLCOLOR, "TreeView", 0, 0, IDENTIFIER.FILLCOLOR, false),
            ("Explorer::TreeView", 0, 0, IDENTIFIER.BORDERCOLOR, "TreeView", 0, 0, IDENTIFIER.BORDERCOLOR, false),
            // the win10 shellstyle pads the tree host 1px left / 3px right and paints the padding with
            // gtc(PROPERTREE, 2, 0, 3802): white strips beside GSM's dark tree. xp had no ProperTree.
            ("ProperTree", 2, 0, IDENTIFIER.FILLCOLOR, "TreeView", 0, 0, IDENTIFIER.FILLCOLOR, false),
            // verified unique-color probe: normal nav labels and drive titles use WINDOWTEXT on win7.
            ("ExplorerNavPane", 0, 0, IDENTIFIER.TEXTCOLOR, "sysmetrics", 0, 0, IDENTIFIER.WINDOWTEXT, true),
            // win10 drive titles use ItemsView's common text, not LISTITEM state text.
            ("ItemsView", 0, 0, IDENTIFIER.TEXTCOLOR, "sysmetrics", 0, 0, IDENTIFIER.WINDOWTEXT, true),
            // win10 defview paints the folder list with ItemsView's common fill (GSM's dark window stayed white);
            // xp drew the list in the WINDOW system color.
            ("ItemsView", 0, 0, IDENTIFIER.FILLCOLOR, "sysmetrics", 0, 0, IDENTIFIER.WINDOW, false),
            // verified open-dialog probe: win10 reads PROPERTY filename states; win7 uses WINDOWTEXT.
            ("ItemsView", 4, 2, IDENTIFIER.TEXTCOLOR, "sysmetrics", 0, 0, IDENTIFIER.WINDOWTEXT, true),
            ("ItemsView", 4, 3, IDENTIFIER.TEXTCOLOR, "sysmetrics", 0, 0, IDENTIFIER.WINDOWTEXT, true),
            ("ScrollBar", 11, 0, IDENTIFIER.FILLCOLOR, "sysmetrics", 0, 0, IDENTIFIER.BTNFACE, false),
            // verified win10 consumer: the explorer probe reads both at 0/0. the win7 role mapping is inferred.
            ("ExplorerStatusBar", 0, 0, IDENTIFIER.FILLCOLOR, "sysmetrics", 0, 0, IDENTIFIER.WINDOW, false),
            ("ExplorerStatusBar", 0, 0, IDENTIFIER.TEXTCOLOR, "Status", 0, 0, IDENTIFIER.TEXTCOLOR, false),
            // shellstyle remaps, verified by diffing win7's shellstyle UIFILE (1, 1033) against aero11's win10 one:
            // the nav pane (ProperTreeModule/Host) reads gtc(PROPERTREE, 2, 0, 3821) on win7 and 3802 on win10.
            ("ProperTree", 2, 0, IDENTIFIER.FILLCOLOR, "ProperTree", 2, 0, IDENTIFIER.FILLCOLORHINT, true),
            // the reading pane was a gradient of GRADIENTCOLOR1-3 on win7 and is a flat 3802 fill on win10.
            // one color can't hold a gradient, the first stop is the closest.
            ("ReadingPane", 1, 0, IDENTIFIER.FILLCOLOR, "ReadingPane", 1, 0, IDENTIFIER.GRADIENTCOLOR1, true),
            // win10 TaskDialog panes (also the Run box here, which a Windhawk mod draws as one): Luna10 fills them with
            // stock xp's BTNFACE beige. xp's dialogs were the plain BTNFACE, so GSM's dark Run box came out beige.
            ("TaskDialog", 1, 0, IDENTIFIER.FILLCOLOR, "sysmetrics", 0, 0, IDENTIFIER.BTNFACE, false),
            ("TaskDialog", 4, 0, IDENTIFIER.FILLCOLOR, "sysmetrics", 0, 0, IDENTIFIER.BTNFACE, false),
            ("TaskDialog", 8, 0, IDENTIFIER.FILLCOLOR, "sysmetrics", 0, 0, IDENTIFIER.BTNFACE, false),
            ("TaskDialog", 8, 0, IDENTIFIER.BORDERCOLOR, "sysmetrics", 0, 0, IDENTIFIER.BTNFACE, false),
            ("TaskDialog", 9, 0, IDENTIFIER.FILLCOLOR, "sysmetrics", 0, 0, IDENTIFIER.BTNFACE, false),
            ("TaskDialog", 15, 0, IDENTIFIER.FILLCOLOR, "sysmetrics", 0, 0, IDENTIFIER.BTNFACE, false),
        };

        /// <summary>
        /// Template-only records (aero11 additions) that hide a value the source changed further up
        /// the inheritance chain get the source's value copied in, never deleted. Explorer::Button
        /// doesn't exist on win7, inherits Button, and adds its own PUSHBUTTON text colors that would
        /// otherwise beat the source's Button colors. Colors are copied in any class; other scalar
        /// types only in win10-only classes (records with a win7 counterpart went through the merge).
        /// Pictures and strip geometry stay the template's, they describe the template's art. A
        /// win10-only class also gets the source's other states of a copied property where they
        /// differ from its state 0, since win10 falls back to the copied state 0 instead of the
        /// base class's state.
        /// </summary>
        private void CopyIntoTemplateOverrides()
        {
            foreach (var a in m_target.Entries.ToList())
            {
                if (a.ResourceType != null || GeometryIds.Contains(a.NameId) || m_touched.Contains(a.Property)
                    || m_base.ByKey.ContainsKey(a.Key) || m_source.ByKey.ContainsKey(a.Key)
                    || a.ClassName.StartsWith("DarkMode", StringComparison.OrdinalIgnoreCase)
                    || m_target.DuplicateClassNames.Contains(a.ClassName)
                    || string.Equals(a.ClassName, DwmClass, StringComparison.OrdinalIgnoreCase)
                    || KeyPattern.Matches(m_opt.Exclude, a.ClassName, a.PartName, a.PropertyName)) continue;
                bool win10Only = !m_base.ClassesByName.ContainsKey(a.ClassName);
                if (a.TypeId != (int)IDENTIFIER.COLOR && !win10Only) continue;

                // nearest class the source knows: itself, or the first template ancestor win7 had.
                var tc = m_target.ClassesByName[a.ClassName];
                var seen = new HashSet<int>();
                while (!m_source.ClassesByName.ContainsKey(tc.ClassName) && seen.Add(tc.ClassId)
                    && tc.BaseClassId >= 0 && m_target.Style.Classes.TryGetValue(tc.BaseClassId, out var parent))
                    tc = parent;
                if (!m_source.ClassesByName.TryGetValue(tc.ClassName, out var sc)
                    || !m_base.ClassesByName.TryGetValue(tc.ClassName, out var bc)) continue;
                int part = a.PartId;
                if (a.PartNameKnown)
                {
                    var parts = SemanticModel.PartNames(sc.ClassName, m_source.Platform).Where(p => p.Value == a.PartName).Select(p => p.Key).ToList();
                    if (parts.Count != 1) continue;
                    part = parts[0];
                }
                var sp = SemanticModel.FindEffective(m_source.Style, sc, part, a.StateId, (IDENTIFIER)a.NameId);
                var bp = SemanticModel.FindEffective(m_base.Style, bc, part, a.StateId, (IDENTIFIER)a.NameId);
                if (sp == null || sp.Header.typeID != a.TypeId) continue;
                // a non-color value only found through the part-0 fallback describes the whole control, not
                // this part: TangoLux's Combobox common BGTYPE=imagefile turned Address::ComboBox
                // TRANSPARENTBACKGROUND (BGTYPE none, a part xp never had) into the combo border image,
                // and the address bar stuck out. colors still follow through the fallback.
                if (a.TypeId != (int)IDENTIFIER.COLOR && part != 0
                    && !m_source.Entries.Any(e => e.ClassName == sc.ClassName && e.PartId == part && e.NameId == a.NameId)) continue;
                bool sourceChanged = bp == null || SemanticModel.FormatValue(sp) != SemanticModel.FormatValue(bp);
                if (sourceChanged)
                    CopyInto(a, sp, "copied-into-override", $"template-only {a.PropertyName} hid the source's {sc.ClassName} value; copied it in");

                if (win10Only && a.StateId == 0) AddSourceStates(a, sc, bc, part);
            }
        }

        private bool CopyInto(SemanticEntry a, StyleProperty sp, string action, string reason)
        {
            var s = m_source.Entries.First(e => ReferenceEquals(e.Property, sp));
            if (Same(m_source, s, m_target, a)) return false;
            if (!ApplySourceValue(m_source, s, a.Property, out string err))
            {
                m_report.Warnings.Add($"{a.ClassName}|{a.PartName}|S{a.StateId}|{a.PropertyName}: not copied, {err}");
                return false;
            }
            m_touched.Add(a.Property);
            ChangedProperties[a.Property] = $"{a.ClassName}|{a.PartName}|S{a.StateId}|{a.PropertyName}";
            Decide(a, action, reason, null, s, a, SemanticModel.FormatValue(a.Property));
            m_report.Count(action);
            return true;
        }

        /// <summary>
        /// For a win10-only class's state-0 record: where the source's ancestor has a different value
        /// in another state of the same part, and the class has no record there, add one.
        /// </summary>
        private void AddSourceStates(SemanticEntry a, StyleClass sc, StyleClass bc, int sourcePart)
        {
            if (!sc.Parts.TryGetValue(sourcePart, out var spart)) return;
            var s0 = SemanticModel.FindEffective(m_source.Style, sc, sourcePart, 0, (IDENTIFIER)a.NameId);
            var cls = m_target.Style.Classes[a.ClassId];
            var tpart = cls.Parts[a.PartId];
            foreach (int state in spart.States.Keys.Where(k => k != 0).OrderBy(k => k))
            {
                if (tpart.States.TryGetValue(state, out var existing) && existing.Properties.Any(p => p.Header.nameID == a.NameId)) continue;
                var sv = SemanticModel.FindEffective(m_source.Style, sc, sourcePart, state, (IDENTIFIER)a.NameId);
                if (sv == null || sv.Header.typeID != a.TypeId) continue;
                if (s0 != null && SemanticModel.FormatValue(sv) == SemanticModel.FormatValue(s0)) continue;
                var bv = SemanticModel.FindEffective(m_base.Style, bc, sourcePart, state, (IDENTIFIER)a.NameId);
                var tv = SemanticModel.FindEffective(m_target.Style, cls, a.PartId, state, (IDENTIFIER)a.NameId);
                bool sourceStock = bv != null && SemanticModel.FormatValue(sv) == SemanticModel.FormatValue(bv);
                // stock win7 had this per-state value too and the template already shows it: nothing to fix.
                if (sourceStock && tv != null && SemanticModel.FormatValue(tv) == SemanticModel.FormatValue(bv)) continue;
                // xp ports were written for win10, so a win10-only class showing something else in a state
                // the source left stock is the port author's choice (Luna's *GoComposited::Toolbar pressed text).
                if (sourceStock && m_source.Platform == Platform.WinXP) continue;
                if (KeyPattern.Matches(m_opt.Exclude, a.ClassName, a.PartName, a.PropertyName)) continue;
                var s = m_source.Entries.First(e => ReferenceEquals(e.Property, sv));
                var prop = new StyleProperty((PropertyHeader)a.Property.Header.Clone());
                prop.Header.stateID = state;
                CopyValue(a.Property, prop);
                if (!ApplySourceValue(m_source, s, prop, out string err))
                {
                    m_report.Warnings.Add($"{a.ClassName}|{a.PartName}|S{state}|{a.PropertyName}: not added, {err}");
                    continue;
                }
                if (!tpart.States.TryGetValue(state, out var st))
                {
                    st = new StyleState { StateId = state, StateName = "State " + state };
                    tpart.States.Add(state, st);
                }
                st.Properties.Add(prop);
                MarkTouched(prop, $"{a.ClassName}|{a.PartName}|S{state}|{a.PropertyName}");
                AddedRecords.Add($"{cls.ClassId}/{a.PartId}/{state}/{prop.Header.nameID}/{prop.Header.typeID}");
                Decide(s, "added-state-copy", $"win10-only {a.ClassName} would fall back to its own state 0; copied the source's {s.ClassName} state {state} value",
                    null, s, null, SemanticModel.FormatValue(prop));
                m_report.Count("added-state-copy");
            }
        }

        private void MirrorIntoWin10Classes()
        {
            foreach (var m in Win10Mirrors)
            {
                if (m_target.DuplicateClassNames.Contains(m.Class) || KeyPattern.Matches(m_opt.Exclude, m.Class, "*", m.Prop.ToString())) continue;
                var a = m_target.Entries.FirstOrDefault(e => e.ClassName == m.Class && e.PartId == m.Part && e.StateId == m.State && e.NameId == (int)m.Prop);
                if (a == null) continue;
                // role remaps replace copied colors too, but never resurrect a source deletion.
                if (!m_target.Style.Classes[a.ClassId].Parts[a.PartId].States[a.StateId].Properties.Contains(a.Property)) continue;
                if (!m_source.ClassesByName.TryGetValue(m.FromClass, out var sc) || !m_base.ClassesByName.TryGetValue(m.FromClass, out var bc)) continue;
                var sp = SemanticModel.FindEffective(m_source.Style, sc, m.FromPart, m.FromState, m.FromProp);
                var bp = SemanticModel.FindEffective(m_base.Style, bc, m.FromPart, m.FromState, m.FromProp);
                if (sp == null) continue;
                // only what the source changed from stock; untouched stock values leave the template alone.
                if (bp != null && SemanticModel.FormatValue(sp) == SemanticModel.FormatValue(bp)) continue;
                var s = m_source.Entries.First(e => ReferenceEquals(e.Property, sp));
                if (Same(m_source, s, m_target, a)) continue;
                ApplyExtra(a, s, null, "applied-mirror",
                    $"{(m.Verified ? "VERIFIED" : "INFERRED")} win10 role mapping from {s.ClassName}|{s.PartName}|S{s.StateId}|{s.PropertyName}");
            }
        }

        // template IMAGE id -> ids it was replaced with by the merge.
        private readonly Dictionary<int, HashSet<int>> m_imageReplacements = new Dictionary<int, HashSet<int>>();

        private void RecordImageReplacement(SemanticEntry a)
        {
            if (a.ResourceType != "IMAGE") return;
            int now = a.Property.Header.shortFlag & 0xFFFF;
            if (now == a.ResourceId) return;
            if (!m_imageReplacements.TryGetValue(a.ResourceId, out var set)) m_imageReplacements[a.ResourceId] = set = new HashSet<int>();
            set.Add(now);
        }

        /// <summary>
        /// xp only: the ports moved some stock pictures to another slot of the same part and state
        /// (TrayNotifyHoriz::Button: xp IMAGEFILE, the Luna port IMAGEFILE1 of a dpi set). The merge
        /// then sees the source's IMAGEFILE as a property the template dropped. A template picture
        /// that is pixel-equal to stock xp's picture of another slot in the same part/state follows
        /// the source's version of that slot, if the source changed it and it fits the template's frames.
        /// </summary>
        private void FollowMovedImages(Dictionary<string, string> locked)
        {
            foreach (var a in m_target.Entries.Where(e => e.ResourceType == "IMAGE" && !m_touched.Contains(e.Property)
                && !locked.ContainsKey(e.PartKey) && !m_target.DuplicateClassNames.Contains(e.ClassName)
                && !KeyPattern.Matches(m_opt.Exclude, e.ClassName, e.PartName, e.PropertyName)).ToList())
            {
                if (m_base.ByKey.TryGetValue(a.Key, out var own) && Same(m_base, own, m_target, a)) continue;
                var stock = m_base.Entries.FirstOrDefault(b => b.ResourceType == "IMAGE" && b.ClassName == a.ClassName
                    && b.PartName == a.PartName && b.StateId == a.StateId && b.NameId != a.NameId && Same(m_base, b, m_target, a));
                if (stock == null || !m_source.ByKey.TryGetValue(stock.Key, out var s) || Same(m_base, stock, m_source, s)) continue;
                ApplyExtra(a, s, stock, "applied-moved-slot",
                    $"template moved the stock {stock.PropertyName} picture to {a.PropertyName}; took the source's {stock.PropertyName}");
            }
        }

        /// <summary>
        /// Second pass over template images nobody touched, including template-only properties
        /// (win10 classes, dark mode copies, extra dpi sizes):
        /// 1. the picture is a stock win7 picture (pixel compare) -> the source's version of it;
        /// 2. the property uses the same IMAGE id as a property the merge already replaced -> the
        ///    same replacement, as long as every replacement of that id agrees.
        /// Locked geometry parts and DWMWindow (atlas based) are left out.
        /// </summary>
        private void PropagateImages(Dictionary<string, string> locked)
        {
            var r = m_report;
            IEnumerable<SemanticEntry> Candidates() => m_target.Entries.Where(e =>
                e.ResourceType == "IMAGE"
                && !m_touched.Contains(e.Property)
                && !string.Equals(e.ClassName, DwmClass, StringComparison.OrdinalIgnoreCase)
                && !m_target.DuplicateClassNames.Contains(e.ClassName)
                && !locked.ContainsKey(e.PartKey)
                && !KeyPattern.Matches(m_opt.Exclude, e.ClassName, e.PartName, e.PropertyName)
                // still the stock picture of its own key: the three-way merge already decided it, e.g. a
                // source keeps RESTOREBUTTON stock while the template merely shares its id with MAXBUTTON.
                && !(m_base.ByKey.TryGetValue(e.Key, out var own) && Same(m_base, own, m_target, e)));

            foreach (var e in Candidates().ToList())
            {
                var alias = FindImageAlias(e);
                if (alias == null || Same(m_source, alias, m_target, e)) continue;
                m_base.ByKey.TryGetValue(e.Key, out var b);
                ApplyExtra(e, alias, b, "applied-stock-copy",
                    $"template shows the stock {alias.ClassName}|{alias.PartName}|{alias.PropertyName} picture here; took the source's version of it");
            }

            foreach (var e in Candidates().ToList())
            {
                if (!m_imageReplacements.TryGetValue(e.ResourceId, out var set)) continue;
                if (set.Count != 1)
                {
                    r.Warnings.Add($"{e.ClassName}|{e.PartName}|S{e.StateId}|{e.PropertyName}: shares IMAGE {e.ResourceId} with properties that got different pictures ({string.Join(", ", set)}); left as is.");
                    continue;
                }
                int id = set.First();
                // the replacement is a picture this run added: its bytes are in the allocator.
                if (m_source.Platform == Platform.WinXP && m_alloc.Added.TryGetValue("IMAGE/" + id, out var bytes)
                    && !FitsTargetFrames(m_source, null, e.Property, ImageIdentity.Analyze(bytes), out string why))
                {
                    r.Warnings.Add($"{e.ClassName}|{e.PartName}|S{e.StateId}|{e.PropertyName}: shares IMAGE {e.ResourceId} with replaced properties, but {why}.");
                    continue;
                }
                e.Property.SetValue(id);
                MarkTouched(e.Property, $"{e.ClassName}|{e.PartName}|S{e.StateId}|{e.PropertyName}");
                Decide(e, "applied-shared-id", $"uses IMAGE {e.ResourceId} like properties that were replaced; follows them to {id}", null, null, e, id.ToString());
                r.Count("applied-shared-id");
            }
        }

        private void ApplyExtra(SemanticEntry a, SemanticEntry from, SemanticEntry b, string action, string reason)
        {
            if (!ApplySourceValue(m_source, from, a.Property, out string err))
            {
                m_report.Warnings.Add($"{a.ClassName}|{a.PartName}|S{a.StateId}|{a.PropertyName}: not applied, {err}");
                return;
            }
            MarkTouched(a.Property, $"{a.ClassName}|{a.PartName}|S{a.StateId}|{a.PropertyName}");
            RecordImageReplacement(a);
            Decide(a, action, reason, b, from, a, SemanticModel.FormatValue(a.Property));
            m_report.Count(action);
        }

        // a strip image, its frame count, layout and nine-grid margins only make sense together.
        private static readonly HashSet<int> GeometryIds = new HashSet<int>
        {
            (int)IDENTIFIER.IMAGEFILE, (int)IDENTIFIER.IMAGEFILE1, (int)IDENTIFIER.IMAGEFILE2, (int)IDENTIFIER.IMAGEFILE3,
            (int)IDENTIFIER.IMAGEFILE4, (int)IDENTIFIER.IMAGEFILE5, (int)IDENTIFIER.IMAGEFILE6, (int)IDENTIFIER.IMAGEFILE7,
            (int)IDENTIFIER.IMAGECOUNT, (int)IDENTIFIER.IMAGELAYOUT, (int)IDENTIFIER.SIZINGMARGINS,
        };

        // IMAGEFILE1..7: the same picture at different dpi, picked by IMAGESELECTTYPE.
        private static readonly HashSet<int> DpiSlotIds = new HashSet<int>
        {
            (int)IDENTIFIER.IMAGEFILE1, (int)IDENTIFIER.IMAGEFILE2, (int)IDENTIFIER.IMAGEFILE3, (int)IDENTIFIER.IMAGEFILE4,
            (int)IDENTIFIER.IMAGEFILE5, (int)IDENTIFIER.IMAGEFILE6, (int)IDENTIFIER.IMAGEFILE7,
        };

        // parts whose frame count/layout/margins the source changed and nothing locked: they take
        // the source's whole strip geometry, extra dpi slots included.
        private HashSet<string> m_scalarChanges = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string> m_sourceGeometryParts = new HashSet<string>(StringComparer.Ordinal);
        // xp: parts whose template geometry was retuned around stock art the source replaced; the source's numbers win there.
        private HashSet<string> m_stockArtParts = new HashSet<string>(StringComparer.Ordinal);
        // xp: parts where prefer-source takes the source's *stock* picture over the template's redraw; their strip
        // geometry follows that picture too (see ThreeWay), or the stock picture would be cut with the template's numbers.
        private HashSet<string> m_preferStockArtParts = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Parts (class|part) where taking the source's strip geometry piecemeal would mix it with
        /// the template's. Each image is its own picture, so one kept image doesn't hold the others
        /// back. What has to stay together is the frame count/layout/margins and the pictures cut
        /// by them: a part is locked when one of those scalars is a kept conflict, or when the
        /// source changes a scalar while some image in the part stays the template's (a kept
        /// conflict, or a template-only image that isn't a dpi slot). Template-only dpi slots
        /// (aero11's IMAGEFILE4-7) don't lock: <see cref="FillDpiSlots"/> gives them the source's
        /// largest picture. Locked parts keep all their geometry from the template.
        /// </summary>
        private Dictionary<string, string> LockedGeometryParts(IEnumerable<string> keys)
        {
            var locked = new Dictionary<string, string>(StringComparer.Ordinal);
            var keptImage = new Dictionary<string, string>(StringComparer.Ordinal);
            var scalarChange = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in m_target.Entries.Where(e => e.ResourceType == "IMAGE" && !DpiSlotIds.Contains(e.NameId) && !m_base.ByKey.ContainsKey(e.Key)))
                keptImage[e.PartKey] = e.PropertyName + " (template only)";

            // prefer-source art: the source kept xp's own picture, the template redrew that slot, and the source's
            // picture still cuts into the template's frame count. Those parts take the source picture and, with it,
            // the source's own strip geometry (ExplorerBar's single-picture group headers and backgrounds).
            if (m_source.Platform == Platform.WinXP && m_opt.PreferSource.Count > 0)
            {
                foreach (var e in m_target.Entries.Where(e => e.ResourceType == "IMAGE" && !m_touched.Contains(e.Property)))
                {
                    if (!m_base.ByKey.TryGetValue(e.Key, out var b) || !m_source.ByKey.TryGetValue(e.Key, out var se)) continue;
                    if (se.ResourceType != "IMAGE" || Same(m_base, b, m_source, se) || Same(m_base, b, m_target, e)) continue;
                    if (!KeyPattern.Matches(m_opt.PreferSource, e.ClassName, e.PartName, e.PropertyName)) continue;
                    if (!SameFrameCut(se, e)) continue;
                    m_preferStockArtParts.Add(e.PartKey);
                }
            }

            foreach (var key in keys)
            {
                if (!m_base.ByKey.TryGetValue(key, out var b) || !m_source.ByKey.TryGetValue(key, out var s)) continue;
                if (!GeometryIds.Contains(b.NameId) || Same(m_base, b, m_source, s)) continue;
                if (!m_target.ByKey.TryGetValue(key, out var a) || Same(m_source, s, m_target, a)) continue;
                if (KeyPattern.Matches(m_opt.PreferSource, b.ClassName, b.PartName, b.PropertyName)) continue;

                bool conflict = !Same(m_base, b, m_target, a);
                // a picture the source replaced never holds the part back, even where the template redrew
                // it: once the part takes the source's geometry, ThreeWay hands it the source's picture too
                // (applied-picture-with-geometry). only template art the source left alone (template-only
                // slots above) would end up cut with the wrong numbers.
                if (b.ResourceType == "IMAGE") continue;
                // xp: the template retuned a number but still draws stock pictures in this part
                // (Luna10 TrayNotifyHoriz::TrayNotify SIZINGMARGINS l34 -> l20 on the stock strip). that
                // number was fitted to stock art; with the source's own pictures, the source's numbers fit.
                if (conflict && (StockArtReplacedBySource(b.ClassName, b.PartName) || SourceReplacedEveryPicture(b.ClassName, b.PartName)))
                {
                    conflict = false;
                    m_stockArtParts.Add(b.PartKey);
                }
                if (conflict && m_preferStockArtParts.Contains(b.PartKey))
                    conflict = false;
                if (conflict)
                    locked[b.PartKey] = $"{b.PropertyName} in this part is a template adaptation, so the part's strip geometry stays with the template";
                else
                    scalarChange[b.PartKey] = b.PropertyName;
            }

            foreach (var kv in scalarChange)
            {
                if (!locked.ContainsKey(kv.Key) && keptImage.TryGetValue(kv.Key, out var img))
                    locked[kv.Key] = $"source changes {kv.Value} but {img} here stays the template's, so the part's strip geometry stays with the template";
            }
            m_scalarChanges = new HashSet<string>(scalarChange.Keys, StringComparer.Ordinal);
            return locked;
        }

        /// <summary>
        /// xp only: every picture of the part in the template is still the stock one of its own key, and
        /// the source replaced at least one of them. ExplorerBar is left out: its task pane art comes from
        /// the shellstyle (<see cref="XpShellImport"/>), not the msstyles pictures.
        /// </summary>
        private bool StockArtReplacedBySource(string cls, string part)
        {
            if (m_source.Platform != Platform.WinXP || string.Equals(cls, "ExplorerBar", StringComparison.OrdinalIgnoreCase)) return false;
            var imgs = m_target.Entries.Where(t => t.ResourceType == "IMAGE" && t.ClassName == cls && t.PartName == part).ToList();
            if (imgs.Count == 0) return false;
            bool replaced = false;
            foreach (var t in imgs)
            {
                if (!m_base.ByKey.TryGetValue(t.Key, out var b) || !Same(m_base, b, m_target, t)) return false;
                if (m_source.ByKey.TryGetValue(t.Key, out var s) && !Same(m_base, b, m_source, s)) replaced = true;
            }
            return replaced;
        }

        /// <summary>
        /// xp only: the source drew its own picture for every picture stock xp has in this part. The template's
        /// retuned numbers were fitted to its own redrawn art, which won't be shown, so the part is cut with the
        /// source's numbers around the source's pictures. Lion OS X's TrackBar THUMBBOTTOM: Luna10 redrew the thumb
        /// and moved its SIZINGMARGINS, so the part stayed locked and showed Luna's slider thumb under Lion.
        /// </summary>
        private bool SourceReplacedEveryPicture(string cls, string part)
        {
            if (m_source.Platform != Platform.WinXP || string.Equals(cls, "ExplorerBar", StringComparison.OrdinalIgnoreCase)) return false;
            var imgs = m_base.Entries.Where(b => b.ResourceType == "IMAGE" && b.ClassName == cls && b.PartName == part).ToList();
            return imgs.Count > 0 && imgs.All(b => m_source.ByKey.TryGetValue(b.Key, out var s) && s.ResourceType == "IMAGE" && !Same(m_base, b, m_source, s));
        }

        /// <summary>
        /// Template-only dpi slots (IMAGEFILE4-7 in aero11) in a state whose pictures came from the
        /// source would show the template's art at high dpi, cut with the source's geometry. They
        /// get the source's largest picture of that state instead, the same thing aero11 does for
        /// GRIPPERVERT (IMAGEFILE4-6 = IMAGEFILE3).
        /// </summary>
        private void FillDpiSlots(Dictionary<string, string> locked)
        {
            var slots = m_target.Entries.Where(e =>
                e.ResourceType == "IMAGE" && DpiSlotIds.Contains(e.NameId)
                && !m_touched.Contains(e.Property)
                && !m_base.ByKey.ContainsKey(e.Key)
                && !string.Equals(e.ClassName, DwmClass, StringComparison.OrdinalIgnoreCase)
                && !m_target.DuplicateClassNames.Contains(e.ClassName)
                && !locked.ContainsKey(e.PartKey)
                && !KeyPattern.Matches(m_opt.Exclude, e.ClassName, e.PartName, e.PropertyName)).ToList();
            foreach (var e in slots)
            {
                // only states whose pictures the import replaced: untouched parts keep aero11's high dpi art.
                bool replaced = m_target.Entries.Any(o => o.PartKey == e.PartKey && o.StateId == e.StateId
                    && o.ResourceType == "IMAGE" && (o.NameId == (int)IDENTIFIER.IMAGEFILE || DpiSlotIds.Contains(o.NameId))
                    && m_touched.Contains(o.Property));
                if (!replaced) continue;
                // a part drawn as an image glyph with IMAGESELECTTYPE (Luna10 Toolbar SPLITBUTTONDROPDOWN, the
                // back/forward dropdown arrows) picks its glyph from IMAGEFILE1-7: those slots are glyph sizes
                // and get the source's GLYPHIMAGEFILE. Filling them with the button background left no arrow.
                bool glyphSlots = m_target.Style.Classes.TryGetValue(e.Property.Header.classID, out var tcls)
                    && SemanticModel.FindEffective(m_target.Style, tcls, e.PartId, e.StateId, IDENTIFIER.GLYPHTYPE)?.GetValue() is int gt && gt == 1
                    && SemanticModel.FindEffective(m_target.Style, tcls, e.PartId, e.StateId, IDENTIFIER.IMAGESELECTTYPE) != null;
                var from = glyphSlots
                    ? m_source.Entries.FirstOrDefault(s => s.PartKey == e.PartKey && s.StateId == e.StateId && s.ResourceType == "IMAGE" && s.NameId == (int)IDENTIFIER.GLYPHIMAGEFILE)
                    : m_source.Entries
                        .Where(s => s.PartKey == e.PartKey && s.StateId == e.StateId && s.ResourceType == "IMAGE"
                            && (DpiSlotIds.Contains(s.NameId) || s.NameId == (int)IDENTIFIER.IMAGEFILE))
                        .OrderByDescending(s => DpiSlotIds.Contains(s.NameId) ? s.NameId : 0)
                        .FirstOrDefault();
                if (from == null || Same(m_source, from, m_target, e)) continue;
                ApplyExtra(e, from, null, "applied-dpi-fill",
                    glyphSlots ? "template-only dpi slot of an image glyph in a state that now uses the source's pictures; took the source's GLYPHIMAGEFILE"
                               : $"template-only dpi slot in a state that now uses the source's pictures; took the source's largest ({from.PropertyName})");
            }
        }

        private Dictionary<string, List<SemanticEntry>> m_baseImagesBySize;

        /// <summary>
        /// When the template shows a stock win7 picture that belongs to some other part (aero11's
        /// TOPTABITEM uses the stock TABITEMLEFTEDGE strip), returns the source's entry for that
        /// other part. Several stock parts can share a picture; the source has to agree on what it
        /// became. If it doesn't, the candidates are narrowed to the same part/state/property as the
        /// template property, then additionally to the same base class name (Desktop::ListView ->
        /// ListView, Explorer::ListView...). Null when still ambiguous or not a stock picture.
        /// </summary>
        internal SemanticEntry FindImageAlias(SemanticEntry a)
        {
            if (a == null || a.ResourceType != "IMAGE") return null;
            var ta = Decoded(m_target, "IMAGE", a.ResourceId);
            if (ta == null) return null;

            if (m_baseImagesBySize == null)
            {
                m_baseImagesBySize = new Dictionary<string, List<SemanticEntry>>();
                foreach (var be in m_base.Entries.Where(e => e.ResourceType == "IMAGE"))
                {
                    var img = Decoded(m_base, "IMAGE", be.ResourceId);
                    if (img == null) continue;
                    string k = img.Width + "x" + img.Height;
                    if (!m_baseImagesBySize.TryGetValue(k, out var l)) m_baseImagesBySize[k] = l = new List<SemanticEntry>();
                    l.Add(be);
                }
            }
            if (!m_baseImagesBySize.TryGetValue(ta.Width + "x" + ta.Height, out var sameSize)) return null;

            var pairs = sameSize
                .Where(be => !string.Equals(be.ClassName, DwmClass, StringComparison.OrdinalIgnoreCase))
                .Where(be => NearlyEqual(Decoded(m_base, "IMAGE", be.ResourceId), ta, m_opt.ImageTolerance))
                .Select(be => Tuple.Create(be, m_source.ByKey.TryGetValue(be.Key, out var se) ? se : null))
                .Where(p => p.Item2 != null && p.Item2.ResourceType == "IMAGE")
                .ToList();

            string BaseClass(string c) { int i = c.LastIndexOf("::", StringComparison.Ordinal); return i < 0 ? c : c.Substring(i + 2); }
            // the class the template says this one inherits from (BCMAP), e.g. DarkMode_Explorer::ListView -> Explorer::ListView.
            string parent = m_target.ClassesByName.TryGetValue(a.ClassName, out var tc) && tc.BaseClassId >= 0
                && m_target.Style.Classes.TryGetValue(tc.BaseClassId, out var pc) ? pc.ClassName : null;
            var narrowings = new List<Func<Tuple<SemanticEntry, SemanticEntry>, bool>>
            {
                p => true,
                p => p.Item1.PartName == a.PartName && p.Item1.StateId == a.StateId && p.Item1.PropertyName == a.PropertyName,
                p => p.Item1.PartName == a.PartName && p.Item1.StateId == a.StateId && p.Item1.PropertyName == a.PropertyName
                     && string.Equals(BaseClass(p.Item1.ClassName), BaseClass(a.ClassName), StringComparison.OrdinalIgnoreCase),
                p => p.Item1.PartName == a.PartName && p.Item1.StateId == a.StateId && p.Item1.PropertyName == a.PropertyName
                     && parent != null && string.Equals(p.Item1.ClassName, parent, StringComparison.OrdinalIgnoreCase),
            };
            foreach (var keep in narrowings)
            {
                var sources = pairs.Where(keep).Select(p => p.Item2).ToList();
                if (sources.Count == 0) return null;
                if (sources.All(x => Same(m_source, x, m_source, sources[0]))) return sources[0];
            }
            return null;
        }

        /// <summary>
        /// Shared merge rule. Returns true when the target property was rewritten from the source (or deleted, for colors).
        /// </summary>
        internal bool ThreeWay(string cls, string part, int state, string prop,
            SemanticEntry b, SemanticEntry s, SemanticEntry a, SemanticModel src, bool partNameKnown)
        {
            var r = m_report;
            if (s == null && b != null)
            {
                r.Count("source-removed");
                if (a == null) return false;
                // colors follow the source no matter what, including deleting them: a dark style can drop
                // TaskDialog's panel fills so dialogs fall back to its dark system colors.
                if (a.TypeId == (int)IDENTIFIER.COLOR && !KeyPattern.Matches(m_opt.Exclude, cls, part, prop) && !m_touched.Contains(a.Property))
                {
                    string how = RemoveColorRecord(a);
                    if (how == "removed")
                    {
                        Decide(b, "removed-color", "source deleted this color; colors always follow the source", b, null, a, null);
                        r.Count("removed-color");
                        return true;
                    }
                    if (how == "pinned")
                    {
                        Decide(a, "pinned-color", "source deleted this color, but it's this class's last record; it holds the value the deletion falls back to instead", b, null, a, SemanticModel.FormatValue(a.Property));
                        r.Count("pinned-color");
                        return true;
                    }
                    Decide(b, "kept-target", "source deleted this color, but it's this class's last record and nothing to fall back to; kept", b, null, a, null);
                    r.Count("kept-target-last-record");
                    return false;
                }
                Decide(b, "kept-target", "source removed this property; only color removals are imported", b, null, a, null);
                return false;
            }
            if (s != null && b == null)
            {
                r.Count("source-added");
                if (a != null)
                {
                    if (Same(src, s, m_target, a))
                    {
                        Decide(s, "already-equal", "source and template both added this property with the same value", null, s, a, null);
                        return false;
                    }
                    // the template's own extra (high dpi caption buttons...) only fits the template's
                    // geometry; where the part takes the source's geometry, the source's extra goes too.
                    // colors follow the source no matter what.
                    bool withGeometry = GeometryIds.Contains(s.NameId) && m_sourceGeometryParts.Contains(s.PartKey);
                    bool isColor = a.TypeId == (int)IDENTIFIER.COLOR;
                    if ((withGeometry || isColor) && !KeyPattern.Matches(m_opt.Exclude, cls, part, prop) && !m_touched.Contains(a.Property))
                    {
                        if (!ApplySourceValue(src, s, a.Property, out string addErr))
                        {
                            Decide(s, "failed", addErr, null, s, a, null);
                            r.Count("failed");
                            r.Warnings.Add($"{cls}|{part}|S{state}|{prop}: not applied, {addErr}");
                            return false;
                        }
                        m_touched.Add(a.Property);
                        RecordImageReplacement(a);
                        ChangedProperties[a.Property] = $"{a.ClassName}|{a.PartName}|S{a.StateId}|{a.PropertyName}";
                        string addAction = isColor ? "applied-color" : "applied-with-geometry";
                        Decide(a, addAction, isColor ? "colors always follow the source" : "both added this; the part uses the source's strip geometry, so the source's version goes with it",
                            null, s, a, SemanticModel.FormatValue(a.Property));
                        r.Count(addAction);
                        return true;
                    }
                    Decide(s, "kept-target", "source and template both added this property; template kept", null, s, a, null);
                    return false;
                }
                if (KeyPattern.Matches(m_opt.Exclude, cls, part, prop))
                {
                    Decide(s, "excluded", "matched an exclude rule", null, s, null, null);
                    return false;
                }
                if (TryAddRecord(s, src, out string why))
                {
                    Decide(s, "added", "source sets this where stock win7 has nothing; added the record to the template", null, s, null, null);
                    r.Count("added");
                    return true;
                }
                Decide(s, "skipped-source-added", why, null, s, null, null);
                r.Count("skipped-source-added");
                return false;
            }
            bool sourceUnchanged = Same(m_base, b, src, s);
            // prefer-source: also take the source's *stock* value where the template rewrote it. Without this the
            // three-way rule above wins for a source that kept xp's own picture (a theme built on luna's art, e.g.
            // Pure's ExplorerBar headers) and the port template's redrawn picture stays, so the port keeps the
            // template's look instead of the theme's. Only pictures and their strip geometry qualify: a number the
            // template retuned for its own Win10-only records is not something a stock xp value describes.
            bool preferStockHere = m_preferStockArtParts.Contains(s.PartKey) && GeometryIds.Contains(s.NameId);
            if (sourceUnchanged && a != null && !m_touched.Contains(a.Property)
                && src.Platform == Platform.WinXP && GeometryIds.Contains(s.NameId)
                && !Same(m_base, b, m_target, a)
                && (preferStockHere || KeyPattern.Matches(m_opt.PreferSource, cls, part, prop)))
            {
                if (!ApplySourceValue(src, s, a.Property, out string stockErr))
                {
                    Decide(s, "kept-target", $"prefer-source wants the source's stock {prop} here, but it doesn't fit the template: {stockErr}", b, s, a, null);
                    r.Count("kept-target-conflict");
                    return false;
                }
                m_touched.Add(a.Property);
                RecordImageReplacement(a);
                ChangedProperties[a.Property] = $"{a.ClassName}|{a.PartName}|S{a.StateId}|{a.PropertyName}";
                Decide(a, "applied-stock-over-template", "prefer-source: the source kept stock xp's value and the template adapted it, so the source's is used", b, s, a, SemanticModel.FormatValue(a.Property));
                r.Count("applied-stock-over-template");
                m_report.Notes.Add($"prefer-source: {cls}|{part}|{prop} took the source's stock xp value over the template's adaptation.");
                return true;
            }
            if (sourceUnchanged)
            {
                r.Count("unchanged");
                return false;
            }
            r.Count("source-changed");

            if (KeyPattern.Matches(m_opt.Exclude, cls, part, prop))
            {
                Decide(s, "excluded", "matched an exclude rule", b, s, a, null);
                return false;
            }
            if (a == null)
            {
                // xp: BGTYPE says how the part's records are drawn. the win10 port often never had one (Luna10's
                // Combobox is a plain border fill), so uxtheme's default (BORDERFILL) would ignore the source's
                // IMAGEFILE that does come over: TangoLux's address box lost its frame picture. it's added.
                // the same goes for colors: Luna10 dropped Combobox's FILLCOLOR, and GSM's address box came out white.
                // and for the strip cut: a missing IMAGELAYOUT is horizontal on xp but vertical on win10, so without the
                // source's layout the source's strip is sliced the wrong way (Lion's 3 side-by-side tray frames, where
                // Luna10 dropped TrayNotifyVert's layout record). Its IMAGECOUNT goes along for the same reason.
                bool cut = s.NameId == (int)IDENTIFIER.IMAGELAYOUT || s.NameId == (int)IDENTIFIER.IMAGECOUNT;
                // xp centres some captions (NOOTOR GTK, Xanadu). win10 reads CONTENTALIGNMENT with a left default and
                // Luna10 kept that record only for MAXCAPTION (as 0), so a centred style's maximized title fell back
                // to left while its normal caption stayed centered. the source's non-default alignment is added.
                bool centeredCaption = s.NameId == (int)IDENTIFIER.CONTENTALIGNMENT && string.Equals(cls, "Window", StringComparison.OrdinalIgnoreCase)
                    && s.Property.GetValue() is int align && align != 0;
                if (src.Platform == Platform.WinXP && (s.NameId == (int)IDENTIFIER.BGTYPE || s.TypeId == (int)IDENTIFIER.COLOR || cut || centeredCaption) && TryAddRecord(s, src, out string bgWhy))
                {
                    Decide(s, "added", s.TypeId == (int)IDENTIFIER.COLOR
                        ? "template dropped this color; xp still draws with it, so the source's color was added"
                        : centeredCaption ? "template has no alignment for this caption part; win10 would draw it left, xp aligned it where the source says, so the source's CONTENTALIGNMENT was added"
                        : cut ? "template has no frame layout/count here; win10's default would cut the source's strip differently than xp, so the source's was added"
                        : "template has no BGTYPE here; without it uxtheme ignores the source's image, so the source's BGTYPE was added", b, s, null, null);
                    r.Count("added");
                    return true;
                }
                string why = m_target.ClassesByName.ContainsKey(cls)
                    ? "template has no such property here (removed or renamed for win10)"
                    : "class doesn't exist in the template";
                Decide(s, "skipped-target-missing", why, b, s, null, null);
                r.Count("skipped-target-missing");
                return false;
            }
            if (m_touched.Contains(a.Property))
            {
                return false;
            }
            if (Same(src, s, m_target, a))
            {
                Decide(s, "already-equal", "template already has the source value", b, s, a, null);
                r.Count("already-equal");
                return false;
            }

            bool targetStock = Same(m_base, b, m_target, a);
            bool forced = false;
            // colors always follow the source, even where the template adapted them.
            bool color = !targetStock && a.TypeId == (int)IDENTIFIER.COLOR;
            string aliasNote = null;
            var applyFrom = s;
            bool pictureFollows = false;
            if (!targetStock && !color)
            {
                if (!KeyPattern.Matches(m_opt.PreferSource, cls, part, prop))
                {
                    var alias = ReferenceEquals(src, m_source) ? FindImageAlias(a) : null;
                    if (alias == null)
                    {
                        // a picture the template redrew is still the template's art, but once the part
                        // is cut with the source's frame count and margins the source's picture is the
                        // one that fits them (a 145x43 flyout link area vs the template's 16x16).
                        if (a.ResourceType == "IMAGE" && ReferenceEquals(src, m_source)
                            && !string.Equals(cls, DwmClass, StringComparison.OrdinalIgnoreCase) && GeometryIsSource(s, a))
                            pictureFollows = true;
                        else if (m_stockArtParts.Contains(s.PartKey) && GeometryIds.Contains(s.NameId))
                            forced = true;
                        else
                        {
                            Decide(s, "kept-target", "template changed this away from stock win7 (intentional win10 adaptation)", b, s, a, null);
                            r.Count("kept-target-conflict");
                            return false;
                        }
                    }
                    else
                    {
                        if (Same(src, alias, m_target, a))
                        {
                            Decide(s, "kept-target", $"template uses the stock {alias.PartName} {alias.PropertyName} picture here, and the source left that one alone", b, s, a, null);
                            r.Count("kept-target-alias-unchanged");
                            return false;
                        }
                        // follow the template's choice of stock sprite, with the source's version of it.
                        aliasNote = $"template uses the stock {alias.ClassName}|{alias.PartName}|{alias.PropertyName} picture here; took the source's version of that one";
                        applyFrom = alias;
                    }
                }
                else forced = true;
            }

            string err;
            if (!ApplySourceValue(src, applyFrom, a.Property, out err))
            {
                Decide(s, "failed", err, b, s, a, null);
                r.Count("failed");
                r.Warnings.Add($"{cls}|{part}|S{state}|{prop}: not applied, {err}");
                return false;
            }
            m_touched.Add(a.Property);
            RecordImageReplacement(a);
            ChangedProperties[a.Property] = $"{a.ClassName}|{a.PartName}|S{a.StateId}|{a.PropertyName}";
            string after = SemanticModel.FormatValue(a.Property);
            string note = partNameKnown ? null : "part matched by numeric id (no part name table)";
            string action = color ? "applied-color" : forced ? "applied-override" : aliasNote != null ? "applied-alias" : pictureFollows ? "applied-picture-with-geometry" : "applied";
            Decide(a, action,
                color ? "colors always follow the source, the template's adaptation was replaced"
                    : forced ? "prefer-source rule overrode a template adaptation"
                    : pictureFollows ? "template redrew this picture, but the part uses the source's frame count and margins, so the source's picture goes with them"
                    : aliasNote ?? note ?? "template had the stock value",
                b, applyFrom, a, after);
            r.Count(action);
            if (!partNameKnown) r.Count("applied-numeric-part");
            return true;
        }

        /// <summary>"classId/partId/stateId/nameId/typeId" of records the import created.</summary>
        internal List<string> AddedRecords { get; } = new List<string>();

        /// <summary>same format, colors the source deleted or template-only color overrides removed.</summary>
        internal List<string> RemovedRecords { get; } = new List<string>();

        private void RemoveRecord(SemanticEntry a)
        {
            var state = m_target.Style.Classes[a.ClassId].Parts[a.PartId].States[a.StateId];
            state.Properties.Remove(a.Property);
            m_touched.Add(a.Property);
            RemovedRecords.Add($"{a.ClassId}/{a.PartId}/{a.StateId}/{a.NameId}/{a.TypeId}");
        }

        /// <summary>
        /// true when the source picture splits into the template's effective frame count the same way (used to decide
        /// whether prefer-source may take a stock source picture at all).
        /// </summary>
        private bool SameFrameCut(SemanticEntry s, SemanticEntry t)
        {
            if (!m_source.ClassesByName.TryGetValue(s.ClassName, out var sc)) return false;
            if (!m_target.Style.Classes.TryGetValue(t.ClassId, out var tc)) return false;
            int sCount = SemanticModel.FindEffective(m_source.Style, sc, s.PartId, s.StateId, IDENTIFIER.IMAGECOUNT)?.GetValue() is int a && a > 0 ? a : 1;
            int tCount = SemanticModel.FindEffective(m_target.Style, tc, t.PartId, t.StateId, IDENTIFIER.IMAGECOUNT)?.GetValue() is int bb && bb > 0 ? bb : 1;
            if (sCount != tCount) return false;
            if (sCount == 1) return true;
            int sLayout = SemanticModel.FindEffective(m_source.Style, sc, s.PartId, s.StateId, IDENTIFIER.IMAGELAYOUT)?.GetValue() is int l1 ? l1 : 1;
            int tLayout = SemanticModel.FindEffective(m_target.Style, tc, t.PartId, t.StateId, IDENTIFIER.IMAGELAYOUT)?.GetValue() is int l2 ? l2 : 0;
            return sLayout == tLayout;
        }

        /// <summary>
        /// true when the template part ends up cut like the source's: the source's geometry was taken
        /// for the part, or the effective frame count, layout and margins already agree.
        /// </summary>
        private bool GeometryIsSource(SemanticEntry s, SemanticEntry a)
        {
            if (m_sourceGeometryParts.Contains(a.PartKey)) return true;
            if (!m_source.ClassesByName.TryGetValue(s.ClassName, out var sc)) return false;
            var tc = m_target.Style.Classes[a.ClassId];
            // effective values: a missing IMAGECOUNT is one frame, and a missing IMAGELAYOUT is the platform's
            // default (xp cuts strips horizontally, win10 vertically). Comparing record presence marked parts with
            // one picture as different geometry whenever the template dropped the layout record (Luna10 did on the
            // single-picture ExplorerBar headers), and the merge then kept the template art over a source picture.
            var svc = SemanticModel.FindEffective(m_source.Style, sc, s.PartId, s.StateId, IDENTIFIER.IMAGECOUNT);
            var tvc = SemanticModel.FindEffective(m_target.Style, tc, a.PartId, a.StateId, IDENTIFIER.IMAGECOUNT);
            int sCount = svc?.GetValue() is int c1 && c1 > 0 ? c1 : 1;
            int tCount = tvc?.GetValue() is int c2 && c2 > 0 ? c2 : 1;
            if (sCount != tCount) return false;
            var svl = SemanticModel.FindEffective(m_source.Style, sc, s.PartId, s.StateId, IDENTIFIER.IMAGELAYOUT);
            var tvl = SemanticModel.FindEffective(m_target.Style, tc, a.PartId, a.StateId, IDENTIFIER.IMAGELAYOUT);
            int sLayout = svl?.GetValue() is int l1 ? l1 : 1;                       // xp default: horizontal
            int tLayout = tvl?.GetValue() is int l2 ? l2 : 0;                       // win10 default: vertical
            if (sCount > 1 && sLayout != tLayout) return false;                     // one frame: layout cuts nothing
            var svm = SemanticModel.FindEffective(m_source.Style, sc, s.PartId, s.StateId, IDENTIFIER.SIZINGMARGINS);
            var tvm = SemanticModel.FindEffective(m_target.Style, tc, a.PartId, a.StateId, IDENTIFIER.SIZINGMARGINS);
            if ((svm == null) != (tvm == null)) return false;
            if (svm != null && SemanticModel.FormatValue(svm) != SemanticModel.FormatValue(tvm)) return false;
            return true;
        }

        /// <summary>
        /// Deletes a color record, unless it's the last record of its class. uxtheme only registers
        /// classes that have VARIANT records, and BCMAP parents index that registration order
        /// (18362 LoadClassDataMap / AddIndexInternal), so an emptied class shifts the parent of
        /// every later class. Emptying a template class early in the list dropped Flyout's
        /// FlyoutStyle parent and the tray flyout fell back to the System font. The last record
        /// instead gets the value the deletion would fall back to. Returns "removed", "pinned", or
        /// null when there's nothing to fall back to.
        /// </summary>
        private string RemoveColorRecord(SemanticEntry a)
        {
            var cls = m_target.Style.Classes[a.ClassId];
            int records = cls.Parts.Values.Sum(p => p.States.Values.Sum(st => st.Properties.Count));
            if (records > 1)
            {
                RemoveRecord(a);
                return "removed";
            }
            var state = cls.Parts[a.PartId].States[a.StateId];
            int index = state.Properties.IndexOf(a.Property);
            state.Properties.RemoveAt(index);
            var fallback = SemanticModel.FindEffective(m_target.Style, cls, a.PartId, a.StateId, (IDENTIFIER)a.NameId);
            state.Properties.Insert(index, a.Property);
            if (fallback == null || fallback.Header.typeID != a.TypeId) return null;
            CopyValue(fallback, a.Property);
            m_touched.Add(a.Property);
            ChangedProperties[a.Property] = $"{a.ClassName}|{a.PartName}|S{a.StateId}|{a.PropertyName}";
            return "pinned";
        }

        /// <summary>
        /// Adds a record the source has and stock win7 doesn't (text glow on the address bar, a
        /// font on the tree view...) to the template, in the same class, part (matched by name, or
        /// by number where there's no part table) and state. The class has to exist in the template;
        /// a missing part or state section is created, the serializer keeps sections contiguous.
        /// The value goes through ApplySourceValue, so images are copied, string and font ids are
        /// translated and records whose meaning changed between win7 and win10 are refused.
        /// </summary>
        private bool TryAddRecord(SemanticEntry s, SemanticModel src, out string why)
        {
            why = null;
            if (!m_target.ClassesByName.TryGetValue(s.ClassName, out var cls) || m_target.DuplicateClassNames.Contains(s.ClassName))
            {
                why = "class doesn't exist in the template (or isn't unique)";
                return false;
            }
            // a class without records isn't registered by uxtheme; giving it one shifts every later BCMAP parent.
            if (!cls.Parts.Values.Any(p => p.States.Values.Any(st => st.Properties.Count > 0)))
            {
                why = "the template class has no records; adding the first one would shift uxtheme's BCMAP parents";
                return false;
            }
            int partId = s.PartId;
            if (s.PartNameKnown)
            {
                var byName = SemanticModel.PartNames(cls.ClassName, m_target.Platform).Where(kv => kv.Value == s.PartName).Select(kv => kv.Key).ToList();
                if (byName.Count != 1) { why = $"part {s.PartName} isn't known for {cls.ClassName} in the template"; return false; }
                partId = byName[0];
            }

            cls.Parts.TryGetValue(partId, out var part);
            StyleState state = null;
            part?.States.TryGetValue(s.StateId, out state);
            // a second source of the same addition (dwm alias parts share their win7 part) mustn't add it twice.
            if (state != null && state.Properties.Any(p => p.Header.nameID == s.NameId && p.Header.typeID == s.TypeId))
            {
                why = "the template state already has this property";
                return false;
            }

            var prop = new StyleProperty((PropertyHeader)s.Property.Header.Clone());
            prop.Header.classID = cls.ClassId;
            prop.Header.partID = partId;
            prop.Header.stateID = s.StateId;
            CopyValue(s.Property, prop);
            if (!ApplySourceValue(src, s, prop, out why)) return false;

            if (part == null)
            {
                part = new StylePart { PartId = partId, PartName = s.PartName };
                cls.Parts.Add(partId, part);
            }
            if (state == null)
            {
                state = new StyleState { StateId = s.StateId, StateName = "State " + s.StateId };
                part.States.Add(s.StateId, state);
            }
            state.Properties.Add(prop);
            MarkTouched(prop, $"{s.ClassName}|{s.PartName}|S{s.StateId}|{s.PropertyName}");
            AddedRecords.Add($"{cls.ClassId}/{partId}/{s.StateId}/{prop.Header.nameID}/{prop.Header.typeID}");
            return true;
        }

        internal void Decide(SemanticEntry e, string action, string reason, SemanticEntry b, SemanticEntry s, SemanticEntry a, string result)
        {
            m_report.Decisions.Add(new MergeDecision
            {
                Class = e.ClassName,
                Part = e.PartName,
                State = e.StateId,
                Property = e.PropertyName,
                Action = action,
                Reason = reason,
                Base = b?.RawValue,
                Source = s?.RawValue,
                Target = a?.RawValue,
                Result = result
            });
        }

        internal bool Same(SemanticModel m1, SemanticEntry e1, SemanticModel m2, SemanticEntry e2)
        {
            if (e1 == null || e2 == null) return e1 == e2;
            if (e1.Value == e2.Value) return true;
            if (e1.IsStringRef && e2.IsStringRef)
            {
                // without a string table on one side all that's left is the id.
                if (e1.Value.StartsWith("strid:") || e2.Value.StartsWith("strid:"))
                    return e1.Property.Header.shortFlag == e2.Property.Header.shortFlag;
                if (e1.TypeId == (int)IDENTIFIER.FONT && e2.TypeId == (int)IDENTIFIER.FONT)
                {
                    // compare what uxtheme turns them into, not the spelling ("Cleartype" vs "ClearType").
                    var f1 = FontSpec.Parse(e1.Value.Substring(4));
                    var f2 = FontSpec.Parse(e2.Value.Substring(4));
                    if (f1.Error == null && f2.Error == null) return f1.Equals(f2);
                }
                return string.Equals(e1.Value, e2.Value, StringComparison.OrdinalIgnoreCase);
            }
            if (e1.ResourceType != null && e1.ResourceType == e2.ResourceType && m_opt.ImageTolerance > 0)
            {
                var i1 = Decoded(m1, e1.ResourceType, e1.ResourceId);
                var i2 = Decoded(m2, e2.ResourceType, e2.ResourceId);
                return i1 != null && i2 != null && NearlyEqual(i1, i2, m_opt.ImageTolerance);
            }
            return false;
        }

        internal ArgbImage Decoded(SemanticModel m, string type, int id)
        {
            string k = (ReferenceEquals(m, m_base) ? "b" : ReferenceEquals(m, m_source) ? "s" : "t") + "/" + type + "/" + id;
            if (m_decoded.TryGetValue(k, out var img)) return img;
            var info = m.GetImageInfo(type, id);
            img = info != null && info.Decodable ? ImageIdentity.Decode(m.GetResourceBytes(type, id)) : null;
            m_decoded[k] = img;
            return img;
        }

        /// <summary>
        /// Same size and every premultiplied channel within <paramref name="tol"/>. Comparing
        /// premultiplied values stops nearly transparent pixels from counting as big changes.
        /// </summary>
        public static bool NearlyEqual(ArgbImage x, ArgbImage y, int tol)
        {
            if (x.Width != y.Width || x.Height != y.Height) return false;
            for (int i = 0; i < x.Pixels.Length; ++i)
            {
                uint p = x.Pixels[i], q = y.Pixels[i];
                if (p == q) continue;
                int pa = (int)(p >> 24), qa = (int)(q >> 24);
                if (Math.Abs(pa - qa) > tol) return false;
                for (int sh = 0; sh < 24; sh += 8)
                {
                    int pc = (int)((p >> sh) & 0xFF) * pa / 255;
                    int qc = (int)((q >> sh) & 0xFF) * qa / 255;
                    if (Math.Abs(pc - qc) > tol) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// xp only: a strip picture must divide into the frame count the target property ends up
        /// with. The win10 xp ports re-cut strips into frame counts xp never had (Header HEADERITEM
        /// 5 -> 12 with new art); an xp picture there would be sliced into garbage, so the template's
        /// picture stays. Win7 imports keep their own rule (validator frame waivers).
        /// </summary>
        /// <summary>
        /// xp's band backdrops (rebar, toolbar, status) are often one pixel wide: a vertical strip holding
        /// the band's top edge rows, its fill row and its bottom edge rows, stretched with xp's own
        /// SIZINGMARGINS. win10 keeps the same record but the template's picture is a whole band, drawn with
        /// the template's margins; stretching the strip's edge rows to band height turned Xanadu's menu bar
        /// band black. So the strip is rebuilt into a picture of the template's size: the fill row fills
        /// everything the template's top margin leaves, and the strip's bottom edge rows are sampled into
        /// the template's bottom margin. The art (fill color and bottom edge) survives, the geometry is the
        /// one win10 stretches.
        /// </summary>
        private byte[] RebuildBandPicture(SemanticModel src, SemanticEntry s, StyleProperty target, byte[] data, ImageInfo info, out bool rebuilt)
        {
            rebuilt = false;
            if (info == null || !info.Decodable || info.Width != 1 || info.Height < 3 || info.HasAlpha) return data;
            var tref = SemanticModel.ResourceRef(target, Platform.Win10);
            if (tref == null) return data;
            var tinfo = m_target.GetImageInfo(tref.Item1, tref.Item2);
            if (tinfo == null || !tinfo.Decodable || tinfo.Width < 8 || tinfo.Height < 3) return data;
            if (s == null || !src.Style.Classes.TryGetValue(s.Property.Header.classID, out var sc)) return data;
            if (!(SemanticModel.FindEffective(src.Style, sc, s.PartId, s.StateId, IDENTIFIER.SIZINGMARGINS)?.GetValue() is Margins sm)) return data;
            if (!m_target.Style.Classes.TryGetValue(target.Header.classID, out var tc)
                || !(SemanticModel.FindEffective(m_target.Style, tc, target.Header.partID, target.Header.stateID, IDENTIFIER.SIZINGMARGINS)?.GetValue() is Margins tm)) return data;
            // only the rebar's band backdrop: part 0 is xp's rebar background, part 6 is win10's BACKGROUND.
            if (!tc.ClassName.EndsWith("Rebar", StringComparison.OrdinalIgnoreCase)) return data;
            if (target.Header.partID != 0 && target.Header.partID != 6) return data;
            if (sm.Top <= 0 && sm.Bottom <= 0) return data;
            var column = ImageIdentity.Decode(data);
            if (column.Width != 1) return data;
            int fillRow = Math.Min(Math.Max(sm.Top, 0), column.Height - 1);
            int bottomRows = Math.Min(Math.Max(sm.Bottom, 0), column.Height - 1);
            int band = Math.Max(tm.Bottom, 0);
            band = Math.Min(band, tinfo.Height - 1);
            int top = Math.Min(Math.Max(tm.Top, 0), tinfo.Height - band - 1);
            var wide = new ArgbImage(tinfo.Width, tinfo.Height);
            int fillEnd = wide.Height - band;
            for (int y = 0; y < fillEnd; ++y)
            {
                uint px = column.Pixels[y < top && sm.Top > 0 ? (y * sm.Top / Math.Max(top, 1)) : fillRow];
                for (int x = 0; x < wide.Width; ++x) wide.Pixels[y * wide.Width + x] = px;
            }
            for (int i = 0; i < band; ++i)
            {
                int srcRow = column.Height - bottomRows + (bottomRows == 0 ? 0 : i * bottomRows / band);
                if (srcRow > column.Height - 1) srcRow = column.Height - 1;
                uint px = column.Pixels[srcRow];
                for (int x = 0; x < wide.Width; ++x) wide.Pixels[(fillEnd + i) * wide.Width + x] = px;
            }
            rebuilt = true;
            return ImageIdentity.EncodePngVerified(wide);
        }

        private bool FitsTargetFrames(SemanticModel src, SemanticEntry s, StyleProperty target, ImageInfo info, out string error)
        {
            error = null;
            if (src.Platform != Platform.WinXP || info == null || !info.Decodable || target.Header.nameID == (int)IDENTIFIER.GLYPHIMAGEFILE
                || target.Header.typeID != (int)IDENTIFIER.FILENAME) return true;
            if (!m_target.Style.Classes.TryGetValue(target.Header.classID, out var cls)) return true;
            var countProp = SemanticModel.FindEffective(m_target.Style, cls, target.Header.partID, target.Header.stateID, IDENTIFIER.IMAGECOUNT);
            var layoutProp = SemanticModel.FindEffective(m_target.Style, cls, target.Header.partID, target.Header.stateID, IDENTIFIER.IMAGELAYOUT);
            // the template has no layout record: the source's comes along (see the a == null branch of the merge),
            // so the strip is cut the source's way. Lion's TrayNotifyVert tray: 3 frames side by side.
            if (layoutProp == null && s != null && src.Style.Classes.TryGetValue(s.Property.Header.classID, out var lsc))
                layoutProp = SemanticModel.FindEffective(src.Style, lsc, s.PartId, s.StateId, IDENTIFIER.IMAGELAYOUT);
            int count = countProp?.GetValue() is int c && c > 0 ? c : 1;
            bool horizontal = layoutProp?.GetValue() is int l && l == 1;
            int len = horizontal ? info.Width : info.Height;
            if (len % count == 0) return true;
            // xp drew this very strip with the same cut: uxtheme slices length / count (integer division) on xp and
            // win10 alike, the spare rows are never shown. appows' 53px frame borders with ImageCount 2.
            if (s != null && src.Style.Classes.TryGetValue(s.Property.Header.classID, out var sc)
                && SemanticModel.FindEffective(src.Style, sc, s.PartId, s.StateId, IDENTIFIER.IMAGECOUNT)?.GetValue() is int scount && scount == count
                && (SemanticModel.FindEffective(src.Style, sc, s.PartId, s.StateId, IDENTIFIER.IMAGELAYOUT)?.GetValue() is int sl && sl == 1) == horizontal)
            {
                FrameWaivers.Add(StyleValidator.FrameWaiverKey(info.PixelSha1, count));
                return true;
            }
            error = $"source picture {info.Width}x{info.Height} doesn't split into the template's {count} {(horizontal ? "horizontal" : "vertical")} frames; template picture kept";
            return false;
        }

        /// <summary>Writes the source value into the template property, translating resource and string ids.</summary>
        internal bool ApplySourceValue(SemanticModel src, SemanticEntry s, StyleProperty target, out string error)
        {
            error = null;
            var sp = s.Property;
            if (sp.Header.typeID != target.Header.typeID)
            {
                error = $"type mismatch ({sp.Header.typeID} vs {target.Header.typeID})";
                return false;
            }

            // same record, different handler: IMAGEFILE6/7 are string ids for win7 uxtheme and images
            // for win10 (6.1.7600 vs 19041 GetThemePrimitiveID tables). copying across would point a
            // string id at an image slot or the other way round.
            var targetRes = SemanticModel.ResourceRef(target, Platform.Win10);
            bool targetString = SemanticModel.IsStringRef(target, Platform.Win10);
            if ((s.ResourceType != null) != (targetRes != null) || s.IsStringRef != targetString)
            {
                error = $"{SemanticModel.PropertyName(sp.Header.nameID)} means something else on win10 than on win7 (image vs string id)";
                return false;
            }
            if (s.ResourceType != null)
            {
                var data = src.GetResourceBytes(s.ResourceType, s.ResourceId);
                if (data == null)
                {
                    error = $"source {s.ResourceType} {s.ResourceId} is missing";
                    return false;
                }
                var info = src.GetImageInfo(s.ResourceType, s.ResourceId);
                if (SemanticModel.MustBePng(sp) && (info == null || !info.Decodable))
                {
                    error = $"source {s.ResourceType} {s.ResourceId} isn't a decodable png ({info?.Error})";
                    return false;
                }
                data = RebuildBandPicture(src, s, target, data, info, out bool bandRebuilt);
                if (bandRebuilt)
                {
                    string stripDims = $"{info.Width}x{info.Height}";
                    var band = ImageIdentity.Analyze(data);
                    if (band.Decodable) info = band;
                    string note = $"{s.ClassName}|{s.PartName}|S{s.StateId}|{s.PropertyName}: xp's {stripDims} band strip rebuilt at the template's picture size, so win10 stretches it the way xp did";
                    if (m_bandStripsNoted.Add(s.ResourceType + "/" + s.ResourceId) && !m_report.Notes.Contains(note))
                        m_report.Notes.Add(note);
                }
                if (!FitsTargetFrames(src, s, target, info, out error)) return false;
                int id = PlaceResource(s.ResourceType, s.ResourceId, data, info != null && info.Decodable ? info : null);
                if (sp.Header.typeID == (int)IDENTIFIER.STREAM)
                    target.Header.shortFlag = id; // StyleProperty.SetValue(int) only maps the id for FILENAME-like types
                else
                    target.SetValue(id);
                return true;
            }

            if (s.IsStringRef)
            {
                if (sp.Header.nameID == (int)IDENTIFIER.ATLASINPUTIMAGE && sp.Header.typeID == (int)IDENTIFIER.STRING)
                {
                    // 19041 requires a 16-byte decoded value for an external 8001 string (table 0x171B83924), 7600 didn't.
                    error = "external ATLASINPUTIMAGE string: win10 uxtheme checks its decoded size, win7 didn't; not copied";
                    return false;
                }
                string text = src.ResolveString(sp.Header.shortFlag);
                if (text == null)
                {
                    error = $"source string {sp.Header.shortFlag} not found";
                    return false;
                }
                int id;
                if (sp.Header.typeID == (int)IDENTIFIER.FONT)
                {
                    if (!PlaceFont(text, out id, out error)) return false;
                }
                else
                {
                    id = m_target.FindStringId(text, out bool exact);
                    if (id < 0)
                    {
                        error = $"template has no string \"{text}\"";
                        return false;
                    }
                    string note = $"string \"{text}\" matched template string {id} ignoring case.";
                    if (!exact && !m_report.Notes.Contains(note)) m_report.Notes.Add(note);
                }
                if (sp.Header.typeID == (int)IDENTIFIER.STRING)
                    target.Header.shortFlag = id; // SetValue(int) only maps the id for FILENAME/FONT-like types
                else
                    target.SetValue(id);
                return true;
            }

            CopyValue(sp, target);
            return true;
        }

        /// <summary>true once a font string was added to the template's string tables.</summary>
        internal bool StringsAdded { get; private set; }
        private readonly Dictionary<string, int> m_addedFonts = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>DWMWindow parts whose geometry fails the check but is exactly what the source (or template) ships.</summary>
        internal HashSet<string> GeometryWaivers { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>source images that don't divide evenly into their IMAGECOUNT, keyed with pixels and count.</summary>
        private HashSet<string> FrameWaivers { get; } = new HashSet<string>(StringComparer.Ordinal);

        private void CollectFrameWaivers()
        {
            // win7 drew these as shipped, so the same picture with the same count is a warning in
            // the validator, not a blocker. a mix of source picture and template count still blocks.
            foreach (var e in m_source.Entries)
            {
                if (e.ResourceType != "IMAGE" || e.NameId == (int)IDENTIFIER.GLYPHIMAGEFILE) continue;
                var info = m_source.GetImageInfo(e.ResourceType, e.ResourceId);
                if (info == null || !info.Decodable) continue;
                if (!StyleValidator.FramesFit(m_source, e, info, out int count, out _))
                    FrameWaivers.Add(StyleValidator.FrameWaiverKey(info.PixelSha1, count));
            }
        }

        /// <summary>
        /// Finds the template string id holding the same font (parsed, not spelled the same) or
        /// adds the source's font string to every string table of the template. Refuses strings
        /// uxtheme would reject (it fails the whole theme load on an unknown style word) and fonts
        /// without a face or a positive size, which would wreck the system metrics fonts.
        /// </summary>
        internal bool PlaceFont(string text, out int id, out string error)
        {
            id = -1;
            error = null;
            var font = FontSpec.Parse(text);
            if (font.Error != null) { error = $"font \"{text}\": {font.Error}"; return false; }
            if (!font.IsSane) { error = $"font \"{text}\" isn't usable ({string.Join("; ", font.Warnings)})"; return false; }
            if (text.Length > FontSpec.MaxStringLength) { error = $"font \"{text}\" is longer than uxtheme reads"; return false; }

            var tables = m_target.Style.StringTables;
            foreach (var kv in tables.SelectMany(t => t.Value).OrderBy(kv => kv.Key))
            {
                if (font.Equals(FontSpec.Parse(kv.Value))) { id = kv.Key; return true; }
            }
            if (m_addedFonts.TryGetValue(font.ToString(), out id)) return true;

            if (tables.Count == 0 || tables.All(t => t.Value.Count == 0))
            {
                error = $"template has no string table to add font \"{text}\" to";
                return false;
            }
            // a fresh id above everything in every language, so no existing string moves.
            id = tables.SelectMany(t => t.Value.Keys).Max() + 1;
            string canonical = font.ToString();
            foreach (var t in tables.Values) t[id] = canonical;
            m_addedFonts[canonical] = id;
            StringsAdded = true;
            m_report.Notes.Add($"added font string {id} \"{canonical}\" to the template's string tables (no template string had that font).");
            m_report.Count("font-strings-added");
            return true;
        }

        internal int PlaceResource(string type, int sourceId, byte[] data, ImageInfo info)
        {
            int id = m_alloc.Place(type, data, out bool reused);
            if (!m_report.Resources.Any(x => x.Type == type && x.SourceId == sourceId))
            {
                m_report.Resources.Add(new ResourceMapping
                {
                    Type = type,
                    SourceId = sourceId,
                    TargetId = id,
                    Reused = reused,
                    Bytes = data.Length,
                    Sha1 = ResourceInventory.Sha1Hex(data),
                    Dimensions = info == null ? null : $"{info.Width}x{info.Height}{(info.HasAlpha ? " alpha" : "")}"
                });
            }
            return id;
        }

        internal static void CopyValue(StyleProperty from, StyleProperty to)
        {
            object v = from.GetValue();
            // the typed SetValue overloads keep sizeInBytes in sync for variable sized payloads.
            switch (v)
            {
                case string str: to.SetValue(str); break;
                case List<int> il: to.SetValue(new List<int>(il)); break;
                case List<Color> cl: to.SetValue(new List<Color>(cl)); break;
                case Margins m: to.SetValue(new Margins(m.Left, m.Top, m.Right, m.Bottom)); break;
                case byte[] raw:
                    to.SetValue((byte[])raw.Clone());
                    to.Header.sizeInBytes = from.Header.sizeInBytes;
                    break;
                default: to.SetValue(v); break;
            }
            to.Header.shortFlag = from.Header.shortFlag;
        }

        // ---- accessors for the DWM pass ----------------------------------------------------
        internal SemanticModel Base => m_base;
        internal SemanticModel Source => m_source;
        internal SemanticModel Target => m_target;
        internal ImportReport Report => m_report;
        internal Win7ImportOptions Options => m_opt;
        internal ResourceIdAllocator Allocator => m_alloc;
        internal void MarkTouched(StyleProperty p, string label)
        {
            m_touched.Add(p);
            ChangedProperties[p] = label;
        }

        internal void Untouch(IEnumerable<StyleProperty> props)
        {
            foreach (var p in props)
            {
                m_touched.Remove(p);
                ChangedProperties.Remove(p);
            }
        }
    }

    /// <summary>pixels the validator must find at <see cref="OutputRect"/> in the output DWM atlas.</summary>
    internal class SpriteExpectation
    {
        public string Part { get; set; }
        public Rectangle OutputRect { get; set; }
        public ArgbImage Expected { get; set; }
    }
}
