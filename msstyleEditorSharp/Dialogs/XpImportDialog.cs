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
        /// Installs the scheme's folder and .theme under %WINDIR%\Resources\Themes and applies that .theme, the way
        /// deskn.cpl does: the shell's own handler (<c>rundll32 themecpl.dll,OpenThemeAction</c>), which is what the
        /// .theme's "open" verb runs, so colors, desktop, cursors, sounds and metrics come with the style instead of
        /// only the msstyles. The copy is elevated, but the apply runs from this process on purpose: the previous
        /// implementation launched the theme through ShellExecute, which raced (it returned before themeui was done,
        /// and an elevated launch can leave explorer half-themed), and robocopy over a folder whose files are locked
        /// by the applied style left a half-copied install. So: rename the old folder out of the way first (that works
        /// even while its files are mapped), copy, then verify <c>Themes\CurrentTheme</c> and <c>ThemeManager\DllName</c>
        /// actually point at the installed pair before reporting success.
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

            // one elevated cmd script: move the installed folder aside (renaming works even when the applied style's
            // files are memory-mapped and can't be overwritten), copy the new folder and the .theme.
            string scriptPath = Path.Combine(Path.GetTempPath(), $"xp-install-{stamp}.cmd");
            string script =
                "@echo off\r\n" +
                $"if exist \"{destDir}\" ren \"{destDir}\" \"{styleFolder}.old-{stamp}\"\r\n" +
                $"robocopy \"{r.StyleFolder}\" \"{destDir}\" /E /R:2 /W:1 /NJH /NJS /NP\r\n" +
                $"copy /Y \"{r.ThemePath}\" \"{destTheme}\"\r\n" +
                // older renamed installs pile up because the applied one's files stay mapped; try to drop the unlocked ones.
                $"for /d %%D in (\"{themes}\\{styleFolder}.old-*\") do @rmdir /s /q \"%%D\" 2>nul\r\n" +
                "exit /b 0\r\n";
            try
            {
                File.WriteAllText(scriptPath, script);
                using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
                    p.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return; // elevation declined
            }
            finally
            {
                try { File.Delete(scriptPath); } catch (IOException) { }
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

            string preTheme = RegistryValue(@"Software\Microsoft\Windows\CurrentVersion\Themes", "CurrentTheme");
            string preStyle = RegistryValue(@"Software\Microsoft\Windows\CurrentVersion\ThemeManager", "DllName");
            string preColor = RegistryValue(@"Software\Microsoft\Windows\CurrentVersion\ThemeManager", "ColorName");

            // the .theme handler. same command the shell runs for the "open" verb, so it applies everything the theme
            // names (colors, desktop picture, cursors, sounds, metrics) and not just the visual style.
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("rundll32.exe",
                    $"\"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "themecpl.dll")}\",OpenThemeAction \"{destTheme}\"")
                    { UseShellExecute = false, CreateNoWindow = true }))
                    p.WaitForExit(30000);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not start the theme handler: " + ex.Message, "Install", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // themeui applies on its own thread; report only once the registry shows the installed pair. Windows keeps
            // its own copy of an applied .theme under %LocalAppData%\Microsoft\Windows\Themes, so CurrentTheme names
            // that copy rather than the file in %WINDIR%; the style path is the reliable half.
            string wantColor = r.Scheme?.InternalName;
            string gotTheme = null, gotStyle = null, gotColor = null;
            var deadline = DateTime.Now.AddSeconds(25);
            while (DateTime.Now < deadline)
            {
                gotTheme = RegistryValue(@"Software\Microsoft\Windows\CurrentVersion\Themes", "CurrentTheme");
                gotStyle = ExpandPath(RegistryValue(@"Software\Microsoft\Windows\CurrentVersion\ThemeManager", "DllName"));
                gotColor = RegistryValue(@"Software\Microsoft\Windows\CurrentVersion\ThemeManager", "ColorName");
                if (SameTheme(gotTheme, destTheme) && string.Equals(gotStyle, destStyle, StringComparison.OrdinalIgnoreCase)) break;
                System.Threading.Thread.Sleep(500);
            }
            bool applied = SameTheme(gotTheme, destTheme) && string.Equals(gotStyle, destStyle, StringComparison.OrdinalIgnoreCase);
            if (applied)
            {
                string schemeInfo = string.IsNullOrEmpty(wantColor) || string.Equals(gotColor, wantColor, StringComparison.OrdinalIgnoreCase)
                    ? "" : $"\r\nColor is \"{gotColor}\", the selected scheme is \"{wantColor}\" (the .theme decides the color).";
                MessageBox.Show(this,
                    $"Installed to {destDir} and applied {Path.GetFileName(destTheme)}.{schemeInfo}\r\n" +
                    $"Windows applied its own copy: {gotTheme}",
                    "Install & apply", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this,
                    $"Installed to {destDir}, but applying it didn't take.\r\n" +
                    $"CurrentTheme: {gotTheme ?? "(none)"}\r\nDllName:      {gotStyle ?? "(none)"}\r\n" +
                    (string.Equals(preTheme, gotTheme, StringComparison.OrdinalIgnoreCase) && string.Equals(preStyle, gotStyle, StringComparison.OrdinalIgnoreCase)
                        ? "Nothing changed at all: the .theme handler didn't apply. If this editor runs elevated, apply the .theme from a normal session."
                        : $"Before:       {preTheme}\r\n              {preStyle}"),
                    "Install & apply", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>the applied .theme is Windows' own copy of the file, so the name is what's compared.</summary>
        private static bool SameTheme(string applied, string installed)
        {
            return applied != null && installed != null
                && string.Equals(Path.GetFileName(applied), Path.GetFileName(installed), StringComparison.OrdinalIgnoreCase);
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
    }
}
