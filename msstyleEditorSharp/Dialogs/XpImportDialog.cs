using libmsstyle.Import;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace msstyleEditor
{
    /// <summary>Shows the outcome of an xp theme import: one row per color scheme, its report, and ways to use the result.</summary>
    public class XpImportDialog : Form
    {
        private readonly List<XpSchemeResult> m_results;
        private readonly ListView m_list;
        private readonly TextBox m_log;
        private readonly Button m_open, m_install;

        /// <summary>the written .msstyles the user chose to open in the editor, or null.</summary>
        public string StyleToOpen { get; private set; }

        public XpImportDialog(List<XpSchemeResult> results, string outDir)
        {
            m_results = results;
            int ok = results.Count(r => r.Ok);
            Text = ok == results.Count ? "Windows XP theme imported" : ok == 0 ? "Windows XP import blocked" : "Windows XP theme partly imported";
            Size = new Size(960, 680);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            var summary = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 56,
                Padding = new Padding(8),
                Text = $"{ok} of {results.Count} color schemes ported to {outDir}. Each has a style folder and a .theme.\r\n" +
                       "Install copies the folder to %WINDIR%\\Resources\\Themes (needs admin) and opens the .theme. Offline checks passed; that isn't a render test."
            };

            m_list = new ListView { Dock = DockStyle.Top, Height = 150, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            m_list.Columns.Add("Scheme", 220);
            m_list.Columns.Add("Status", 140);
            m_list.Columns.Add("Applied", 70);
            m_list.Columns.Add("Kept", 70);
            m_list.Columns.Add("Warnings", 70);
            m_list.Columns.Add("Output", 360);
            foreach (var r in results)
            {
                int applied = r.Report.Counts.Where(kv => kv.Key.StartsWith("applied") || kv.Key == "added").Sum(kv => kv.Value);
                int kept = r.Report.Counts.Where(kv => kv.Key.StartsWith("kept-target")).Sum(kv => kv.Value);
                var item = new ListViewItem(new[]
                {
                    r.Scheme.DisplayName.Trim(), r.Report.Status.ToString(), applied.ToString(), kept.ToString(),
                    r.Report.Warnings.Count.ToString(), r.ThemePath ?? string.Join(" ", r.Report.Errors)
                }) { Tag = r };
                m_list.Items.Add(item);
            }

            m_log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9f),
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
            m_open = new Button { Text = "Open in editor", AutoSize = true };
            m_install = new Button { Text = "Install && apply", AutoSize = true };
            var folder = new Button { Text = "Open output folder", AutoSize = true };
            folder.Click += (s, e) => Process.Start("explorer", "\"" + outDir + "\"");
            m_open.Click += (s, e) =>
            {
                if (!(Selected?.StylePath is string p)) return;
                StyleToOpen = p;
                DialogResult = DialogResult.OK;
                Close();
            };
            m_install.Click += (s, e) => Install(Selected);
            buttons.Controls.Add(close);
            buttons.Controls.Add(m_open);
            buttons.Controls.Add(m_install);
            buttons.Controls.Add(folder);
            AcceptButton = close;

            m_list.SelectedIndexChanged += (s, e) => ShowSelected();

            Controls.Add(m_log);
            Controls.Add(m_list);
            Controls.Add(buttons);
            Controls.Add(summary);
            Shown += (s, e) =>
            {
                if (m_list.Items.Count > 0) m_list.Items[0].Selected = true;
                close.Focus();
            };
        }

        private XpSchemeResult Selected => m_list.SelectedItems.Count == 1 ? (XpSchemeResult)m_list.SelectedItems[0].Tag : null;

        private void ShowSelected()
        {
            var r = Selected;
            m_open.Enabled = m_install.Enabled = r?.StylePath != null;
            m_log.Text = r == null ? "" : ImportReportWriter.ToMarkdown(r.Report).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }

        /// <summary>
        /// Installs the scheme's folder and .theme under %WINDIR%\Resources\Themes and applies that .theme.
        /// The copy is elevated; the apply is done in-process by <see cref="XpThemeApply"/>, which calls uxtheme's
        /// <c>SetSystemVisualStyle</c> for the style and then writes/announces the .theme's other sections itself. The
        /// shell handler (<c>themecpl.dll,OpenThemeAction</c>) applies the same things but opens Settings while it does,
        /// and from an elevated process it often does nothing at all (a rundll32 started that way just exits), so it is
        /// not used. Renaming the old folder aside keeps robocopy from failing on the applied style's mapped files.
        /// </summary>
        private void Install(XpSchemeResult r)
        {
            if (r?.StylePath == null) return;
            string themes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Resources", "Themes");
            string styleFolder = Path.GetFileName(r.StyleFolder);
            string destDir = Path.Combine(themes, styleFolder);
            string destTheme = Path.Combine(themes, Path.GetFileName(r.ThemePath));
            string themeFile = Path.GetFileName(r.StylePath);
            string destStyle = Path.Combine(destDir, themeFile);
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");

            // One elevated cmd script. Copying over an applied style fails (uxtheme has its .msstyles mapped), so the old
            // folder goes first; the previous version renamed it aside and then deleted it with a "for /d" glob that also
            // matched the folder it had just copied. Every step's failure is propagated: the old script ended "exit /b 0"
            // no matter what, so a failed copy still reported success here.
            string scriptPath = Path.Combine(Path.GetTempPath(), $"xp-install-{stamp}.cmd");
            string script =
                "@echo off\r\n" +
                $"if exist \"{destDir}\" rmdir /s /q \"{destDir}\" || exit /b 2\r\n" +
                // 0-7 are robocopy success codes; 8 and above are real failures.
                $"robocopy \"{r.StyleFolder}\" \"{destDir}\" /E /R:2 /W:1 /NJH /NJS /NP\r\n" +
                "if errorlevel 8 exit /b 3\r\n" +
                $"copy /Y \"{r.ThemePath}\" \"{destTheme}\" >nul || exit /b 4\r\n" +
                "exit /b 0\r\n";
            int code = -1;
            try
            {
                File.WriteAllText(scriptPath, script);
                using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    p.WaitForExit();
                    code = p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // the elevation prompt was declined: say so instead of reporting a silent "install" that copied nothing.
                MessageBox.Show(this, "Install cancelled: Windows refused the elevation prompt, so nothing was copied to " + themes + ".",
                    "Install", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally
            {
                try { File.Delete(scriptPath); } catch (IOException) { }
            }

            if (code != 0)
            {
                string why = code == 2
                    ? "the previously installed folder couldn't be removed (if this theme is applied right now, Windows locks its files: switch to another theme and install again)"
                    : code == 3 ? "robocopy couldn't copy the style folder"
                    : code == 4 ? "the .theme couldn't be copied"
                    : "the elevated command failed (exit code " + code + ")";
                MessageBox.Show(this, "Install failed: " + why + ". Nothing was changed under " + themes + " except what the script had already done.",
                    "Install", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(destTheme) || !File.Exists(destStyle) || !FilesMatch(r.StylePath, destStyle))
            {
                MessageBox.Show(this, $"Copying to {destDir} failed. If this theme is applied right now, Windows locks its files: switch to another theme and install again.",
                    "Install", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!FilesMatch(r.ThemePath, destTheme))
            {
                MessageBox.Show(this, $"Copying {Path.GetFileName(r.ThemePath)} to {themes} failed.", "Install", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // robocopy's exit code alone doesn't say the whole folder arrived: every file the pack carries has to be there
            // with the same bytes, or an applied theme will draw half its bitmaps from a previous install.
            string missing = FirstMissingFile(r.StyleFolder, destDir);
            if (missing != null)
            {
                MessageBox.Show(this, $"The install is incomplete: {missing}. Switch to another theme and install again.",
                    "Install", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string preTheme = RegistryValue(@"Software\Microsoft\Windows\CurrentVersion\Themes", "CurrentTheme");
            string preStyle = RegistryValue(@"Software\Microsoft\Windows\CurrentVersion\ThemeManager", "DllName");

            // the .theme itself: style through uxtheme, then the sections Windows would apply (colors, desktop, metrics,
            // cursors, sounds, icons). no UI, no Settings window, no cross-process race.
            ThemeApplyResult result;
            try
            {
                result = XpThemeApply.Apply(destTheme);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not apply " + Path.GetFileName(destTheme) + ": " + ex.Message, "Install", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string gotStyle = ExpandPath(RegistryValue(@"Software\Microsoft\Windows\CurrentVersion\ThemeManager", "DllName"));
            string gotColor = RegistryValue(@"Software\Microsoft\Windows\CurrentVersion\ThemeManager", "ColorName");
            bool applied = result.Ok && string.Equals(gotStyle, destStyle, StringComparison.OrdinalIgnoreCase);
            string detail = string.Join("\r\n", result.Notes.Concat(result.Warnings.Select(w => "! " + w)));
            if (applied)
            {
                string schemeInfo = string.IsNullOrEmpty(r.Scheme?.InternalName) || string.Equals(gotColor, r.Scheme.InternalName, StringComparison.OrdinalIgnoreCase)
                    ? "" : $"\r\nColor is \"{gotColor}\", the selected scheme is \"{r.Scheme.InternalName}\" (the .theme decides the color).";
                MessageBox.Show(this,
                    $"Installed to {destDir} and applied {Path.GetFileName(destTheme)}.{schemeInfo}\r\n\r\n{detail}",
                    "Install & apply", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this,
                    $"Installed to {destDir}, but the style did not take.\r\n" +
                    $"DllName: {gotStyle ?? "(none)"}\r\nBefore:  {preStyle ?? "(none)"}\r\nTheme:   {preTheme ?? "(none)"}\r\n\r\n{detail}",
                    "Install & apply", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string RegistryValue(string subKey, string name)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey))
                    return key?.GetValue(name) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ExpandPath(string path)
        {
            return string.IsNullOrEmpty(path) ? path : Environment.ExpandEnvironmentVariables(path);
        }

        private static bool FilesMatch(string a, string b)
        {
            if (!File.Exists(b)) return false;
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }

        /// <summary>the first file of the source folder that isn't in the installed copy with the same bytes, null when
        /// the copy is complete. A failed robocopy can still leave the .msstyles behind, so the style check alone can't
        /// tell whether the shell bitmaps and .mui folders arrived.</summary>
        private static string FirstMissingFile(string sourceDir, string destDir)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(sourceDir.Length).TrimStart('\\', '/');
                    string copy = Path.Combine(destDir, rel);
                    if (!File.Exists(copy)) return rel;
                    if (!FilesMatch(f, copy)) return rel + " (different contents)";
                }
            }
            catch (IOException ex) { return "can't verify the copy: " + ex.Message; }
            catch (UnauthorizedAccessException ex) { return "can't verify the copy: " + ex.Message; }
            return null;
        }
    }
}
