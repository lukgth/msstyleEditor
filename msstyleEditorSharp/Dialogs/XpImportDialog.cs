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
        /// copies the theme's folder (every scheme shares it) to %WINDIR%\Resources\Themes through an elevated robocopy,
        /// then opens the scheme's .theme. a theme in use locks its files, so applying another theme first may be needed.
        /// </summary>
        private void Install(XpSchemeResult r)
        {
            if (r?.StylePath == null) return;
            string themes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Resources", "Themes");
            string destDir = Path.Combine(themes, Path.GetFileName(r.StyleFolder));
            string destTheme = Path.Combine(themes, Path.GetFileName(r.ThemePath));
            string cmd = $"/c robocopy \"{r.StyleFolder}\" \"{destDir}\" /E /R:1 /W:1 /NJH /NJS /NP & copy /Y \"{r.ThemePath}\" \"{destTheme}\"";
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("cmd.exe", cmd) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
                    p.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return; // elevation declined
            }
            if (!File.Exists(destTheme) || !FilesMatch(r.StylePath, Path.Combine(destDir, Path.GetFileName(r.StylePath))))
            {
                MessageBox.Show(this, $"Copying to {destDir} failed. If this theme is applied right now, Windows locks its files: switch to another theme and install again.",
                    "Install", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(destTheme) { UseShellExecute = true });
        }

        private static bool FilesMatch(string a, string b)
        {
            if (!File.Exists(b)) return false;
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }
    }
}
