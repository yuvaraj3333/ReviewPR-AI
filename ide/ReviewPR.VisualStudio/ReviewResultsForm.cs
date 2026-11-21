using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ReviewPR.VisualStudio
{
    internal sealed class ReviewResultsForm : Form
    {
        internal ReviewResultsForm(
            string summary,
            IList<ValidatedFinding> findings,
            int rejected,
            IList<string> rejectionReasons)
        {
            Text = "ReviewPR AI - Review Results";
            Size = new Size(1100, 700);
            MinimumSize = new Size(850, 500);
            StartPosition = FormStartPosition.CenterScreen;

            var summaryLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 65,
                Padding = new Padding(12),
                Text = (summary ?? "AI review completed.") +
                       Environment.NewLine +
                       "Validated findings: " + findings.Count +
                       " | Rejected findings: " + rejected,
                AutoEllipsis = true
            };

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                RowHeadersVisible = false,
                BackgroundColor = SystemColors.Window
            };

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "File",
                DataPropertyName = "FilePath",
                Width = 220
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Line",
                DataPropertyName = "Line",
                Width = 55
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Severity",
                DataPropertyName = "Severity",
                Width = 85
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Category",
                DataPropertyName = "Category",
                Width = 110
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Description",
                DataPropertyName = "Description",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 200
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Suggested Fix",
                DataPropertyName = "SuggestedFix",
                Width = 280
            });

            grid.DataSource = new List<ValidatedFinding>(findings);

            var rejectionPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 110,
                Padding = new Padding(10)
            };

            var rejectionLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 25,
                Text = "Rejected Finding Diagnostics (" + rejected + ")",
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
            };

            var rejectionList = new ListBox
            {
                Dock = DockStyle.Fill,
                HorizontalScrollbar = true
            };

            if (rejectionReasons != null)
            {
                foreach (string reason in rejectionReasons)
                    rejectionList.Items.Add(reason);
            }

            if (rejectionList.Items.Count == 0 && rejected > 0)
                rejectionList.Items.Add("Rejection details unavailable.");

            rejectionPanel.Controls.Add(rejectionList);
            rejectionPanel.Controls.Add(rejectionLabel);

            Controls.Add(grid);
            Controls.Add(rejectionPanel);
            Controls.Add(summaryLabel);
        }
    }
}
