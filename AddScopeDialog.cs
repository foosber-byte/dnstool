using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public static class AddScopeDialog
    {
        public static string Show(string zoneHint)
        {
            using var dlg = new Form
            {
                Text = "Создать scope",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(360, 130),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lblHint = new Label
            {
                Text = $"В зоне: {zoneHint}",
                ForeColor = Color.DimGray,
                Location = new Point(16, 14),
                AutoSize = true
            };

            var lblName = new Label { Text = "Имя scope:", Location = new Point(16, 48), AutoSize = true };
            var txtName = new TextBox { Location = new Point(120, 44), Width = 220 };

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(176, 80), Size = new Size(80, 32) };
            var btnCreate = new Button { Text = "Создать", DialogResult = DialogResult.OK, Location = new Point(260, 80), Size = new Size(80, 32) };

            dlg.Controls.AddRange(new Control[] { lblHint, lblName, txtName, btnCancel, btnCreate });
            dlg.AcceptButton = btnCreate;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return null;
            var name = txtName.Text.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
    }
}
