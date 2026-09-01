using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>Одна цель дублирования политики: конкретный scope конкретной зоны.</summary>
    public sealed class PolicyTarget
    {
        public string Zone;
        public string Scope;
    }

    /// <summary>Что пользователь выбрал в окне дублирования политики.</summary>
    public sealed class DuplicatePolicyPlan
    {
        public string BaseName;
        public bool KeepExactName;                       // пытаться оставить имя как есть (иначе всегда суффикс _<scope>)
        public List<string> Subnets = new List<string>();
        public List<PolicyTarget> Targets = new List<PolicyTarget>();
    }

    /// <summary>
    /// Окно "дублировать политику на другие scope'ы / зоны". Позволяет набрать сразу несколько
    /// целей: выбрать зону, подгрузить её scope'ы, отметить нужные галочками, добавить в список
    /// целей - и повторить для других зон. Для каждой пары (зона, scope) потом создаётся
    /// отдельная политика с тем же критерием-подсетью.
    /// </summary>
    public static class DuplicatePolicyDialog
    {
        public static DuplicatePolicyPlan Show(
            string sourceName,
            string sourceZone,
            IReadOnlyList<string> sourceSubnets,
            IReadOnlyList<string> availableZones,
            IReadOnlyList<string> availableSubnets,
            Func<string, Task<List<string>>> loadScopes)
        {
            var targets = new List<PolicyTarget>();

            using var dlg = new Form
            {
                Text = "Дублировать политику",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(560, 560),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lblSrc = new Label
            {
                Text = $"Оригинал: {sourceName}   (зона: {sourceZone})",
                ForeColor = Color.DimGray,
                Location = new Point(16, 14),
                AutoSize = true
            };

            var lblName = new Label { Text = "Имя новых политик:", Location = new Point(16, 44), AutoSize = true };
            var txtName = new TextBox { Location = new Point(150, 40), Width = 260, Text = sourceName ?? "" };

            var chkExact = new CheckBox
            {
                Text = "Пытаться сохранить точное имя (при совпадении зоны с оригиналом или\n" +
                       "нескольких scope'ах в одной зоне всё равно добавится суффикс \"_<scope>\")",
                Location = new Point(150, 66),
                Size = new Size(390, 40)
            };

            var lblSubnets = new Label { Text = "Подсети (критерий):", Location = new Point(16, 116), AutoSize = true };
            var txtSubnets = new TextBox
            {
                Location = new Point(150, 112),
                Width = 316,
                Text = string.Join(", ", (sourceSubnets ?? new string[0]).Where(s => !string.IsNullOrWhiteSpace(s)))
            };
            var btnPickSubnets = new Button { Text = "Выбрать…", Location = new Point(470, 111), Size = new Size(72, 24) };
            btnPickSubnets.Enabled = availableSubnets != null && availableSubnets.Count > 0;
            btnPickSubnets.Click += (s, e) =>
            {
                var current = txtSubnets.Text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0);
                var picked = CheckedListPickerDialog.Show("Выбор подсетей",
                    "Отметь подсети (сработает по логическому ИЛИ):", availableSubnets, current);
                if (picked != null) txtSubnets.Text = string.Join(", ", picked);
            };

            var grp = new GroupBox { Text = "Куда дублировать", Location = new Point(16, 148), Size = new Size(528, 300) };

            var lblZone = new Label { Text = "Зона:", Location = new Point(12, 28), AutoSize = true };
            var cmbZone = new ComboBox { Location = new Point(60, 24), Width = 300, DropDownStyle = ComboBoxStyle.DropDown };
            foreach (var z in availableZones ?? new string[0]) cmbZone.Items.Add(z);
            var btnLoadScopes = new Button { Text = "Загрузить scope'ы", Location = new Point(370, 23), Size = new Size(140, 24) };

            var clbScopes = new CheckedListBox
            {
                Location = new Point(12, 58),
                Size = new Size(498, 150),
                CheckOnClick = true,
                IntegralHeight = false
            };

            var btnAddTargets = new Button { Text = "Добавить отмеченные в список целей ↓", Location = new Point(12, 214), Size = new Size(280, 26) };

            var lstTargets = new ListBox
            {
                Location = new Point(12, 246),
                Size = new Size(400, 92),
                HorizontalScrollbar = true,   // длинные "зона -> scope" не должны уезжать за край без возможности прокрутки
                ScrollAlwaysVisible = true    // ползунок виден всегда, сразу понятно что список прокручивается
            };
            var btnRemoveTarget = new Button { Text = "Убрать", Location = new Point(420, 246), Size = new Size(90, 26) };

            grp.Controls.AddRange(new Control[] { lblZone, cmbZone, btnLoadScopes, clbScopes, btnAddTargets, lstTargets, btnRemoveTarget });

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(376, 512), Size = new Size(80, 32) };
            var btnOk = new Button { Text = "Создать", DialogResult = DialogResult.OK, Location = new Point(462, 512), Size = new Size(80, 32) };

            const string emptyMark = "(в этой зоне нет scope'ов)";

            btnLoadScopes.Click += async (s, e) =>
            {
                var zone = cmbZone.Text.Trim();
                if (zone.Length == 0) return;
                btnLoadScopes.Enabled = false;
                try
                {
                    var scopes = await loadScopes(zone);
                    clbScopes.Items.Clear();
                    if (scopes == null || scopes.Count == 0)
                    {
                        clbScopes.Items.Add(emptyMark);
                    }
                    else
                    {
                        foreach (var sc in scopes) clbScopes.Items.Add(sc);
                    }
                }
                finally
                {
                    btnLoadScopes.Enabled = true;
                }
            };

            btnAddTargets.Click += (s, e) =>
            {
                var zone = cmbZone.Text.Trim();
                if (zone.Length == 0) return;
                foreach (var obj in clbScopes.CheckedItems)
                {
                    var scope = obj?.ToString();
                    if (string.IsNullOrEmpty(scope) || scope == emptyMark) continue;
                    if (targets.Any(t => t.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase) &&
                                         t.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    targets.Add(new PolicyTarget { Zone = zone, Scope = scope });
                    lstTargets.Items.Add($"{zone}   →   {scope}");
                }
            };

            btnRemoveTarget.Click += (s, e) =>
            {
                var i = lstTargets.SelectedIndex;
                if (i < 0) return;
                lstTargets.Items.RemoveAt(i);
                targets.RemoveAt(i);
            };

            btnOk.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txtName.Text))
                {
                    MessageBox.Show(dlg, "Укажи имя новых политик.", "Дублирование", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    dlg.DialogResult = DialogResult.None;
                    return;
                }
                if (targets.Count == 0)
                {
                    MessageBox.Show(dlg, "Добавь хотя бы одну цель (зона + scope).", "Дублирование", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    dlg.DialogResult = DialogResult.None;
                    return;
                }
            };

            dlg.Controls.AddRange(new Control[]
            {
                lblSrc, lblName, txtName, chkExact, lblSubnets, txtSubnets, btnPickSubnets, grp, btnCancel, btnOk
            });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return null;

            return new DuplicatePolicyPlan
            {
                BaseName = txtName.Text.Trim(),
                KeepExactName = chkExact.Checked,
                Subnets = txtSubnets.Text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList(),
                Targets = targets
            };
        }
    }
}
