using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public static class AddPolicyDialog
    {
        public static (string Name, string Subnets, string Scope) Show(string zoneHint,
            IReadOnlyList<string> availableSubnets = null)
        {
            using var dlg = new Form
            {
                Text = "Создать политику",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(420, 250),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var toolTip = new ToolTip();

            var lblHint = new Label
            {
                Text = $"В зоне: {zoneHint}",
                ForeColor = Color.DimGray,
                Location = new Point(16, 14),
                AutoSize = true
            };

            var lblName = new Label { Text = "Имя политики:", Location = new Point(16, 48), AutoSize = true };
            var txtName = new TextBox { Location = new Point(140, 44), Width = 260 };

            var lblSubnets = new Label { Text = "Подсети:", Location = new Point(16, 84), AutoSize = true };
            var txtSubnets = new TextBox { Location = new Point(140, 80), Width = 158 };
            var btnPickSubnets = new Button { Text = "Выбрать…", Location = new Point(302, 79), Size = new Size(72, 24) };
            var hintSubnets = HelpIcon.Create(toolTip, "Одна или несколько подсетей через запятую (логическое ИЛИ). Только имена подсетей, без описания в скобках (то, что в скобках - не часть имени, добавить как есть будет ошибкой).");
            hintSubnets.Location = new Point(380, 82);

            btnPickSubnets.Enabled = availableSubnets != null && availableSubnets.Count > 0;
            btnPickSubnets.Click += (s, e) =>
            {
                var current = txtSubnets.Text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0);
                var picked = CheckedListPickerDialog.Show("Выбор подсетей",
                    "Отметь подсети (сработает по логическому ИЛИ):", availableSubnets, current);
                if (picked != null) txtSubnets.Text = string.Join(", ", picked);
            };

            var lblScope = new Label { Text = "Scope:", Location = new Point(16, 120), AutoSize = true };
            var txtScope = new TextBox { Location = new Point(140, 116), Width = 260 };

            // Не временное сообщение в логе, а постоянная заметка прямо в диалоге - легко
            // забыть (как выяснилось), а последствие не всегда очевидно в моменте создания.
            var lblReplicationNote = new Label
            {
                Text = "Политики (как и клиентские подсети) НЕ реплицируются на резервные контроллеры " +
                       "домена - привязаны локально к этому серверу/зоне. Поэтому одно и то же имя " +
                       "политики у разных зон - НЕ конфликт.",
                ForeColor = Color.DarkOrange,
                Location = new Point(16, 152),
                Size = new Size(388, 42)
            };

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(236, 200), Size = new Size(80, 32) };
            var btnCreate = new Button { Text = "Создать", DialogResult = DialogResult.OK, Location = new Point(320, 200), Size = new Size(80, 32) };

            dlg.Controls.AddRange(new Control[]
            {
                lblHint, lblName, txtName, lblSubnets, txtSubnets, btnPickSubnets, hintSubnets, lblScope, txtScope,
                lblReplicationNote, btnCancel, btnCreate
            });
            dlg.AcceptButton = btnCreate;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return (null, null, null);
            var name = txtName.Text.Trim();
            var subnets = txtSubnets.Text.Trim();
            var scope = txtScope.Text.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(subnets) || string.IsNullOrEmpty(scope)) return (null, null, null);
            return (name, subnets, scope);
        }
    }
}
