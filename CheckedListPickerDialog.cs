using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>
    /// Универсальное окно "отметь нужное из списка галочками" с множественным выбором.
    /// Используется для выбора подсетей при создании политики и для выбора scope'ов при
    /// её дублировании. Возвращает список отмеченных значений либо null (Отмена).
    /// </summary>
    public static class CheckedListPickerDialog
    {
        public static List<string> Show(string title, string prompt,
            IEnumerable<string> items, IEnumerable<string> preChecked = null)
        {
            var all = (items ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            var pre = new HashSet<string>(preChecked ?? Enumerable.Empty<string>(), System.StringComparer.OrdinalIgnoreCase);

            using var dlg = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(360, 420),
                MinimumSize = new Size(300, 260),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lbl = new Label { Text = prompt, Dock = DockStyle.Top, Height = 34, Padding = new Padding(4) };

            var clb = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false
            };
            foreach (var it in all) clb.Items.Add(it, pre.Contains(it));

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 42,
                Padding = new Padding(4)
            };

            var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(84, 30) };
            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Size = new Size(84, 30) };
            var btnAll = new Button { Text = "Все", Size = new Size(64, 30) };
            var btnNone = new Button { Text = "Снять", Size = new Size(64, 30) };
            btnAll.Click += (s, e) => { for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, true); };
            btnNone.Click += (s, e) => { for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, false); };

            bottom.Controls.AddRange(new Control[] { btnOk, btnCancel, btnNone, btnAll });

            dlg.Controls.Add(clb);
            dlg.Controls.Add(lbl);
            dlg.Controls.Add(bottom);
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return null;
            return clb.CheckedItems.Cast<string>().ToList();
        }
    }
}
