using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public static class AddPolicyDialog
    {
        public static (string Name, string Subnets, string Scope) Show(string zoneHint)
        {
            using var dlg = new Form
            {
                Text = "Создать политику",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(400, 210),
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
            var txtName = new TextBox { Location = new Point(140, 44), Width = 240 };

            var lblSubnets = new Label { Text = "Подсети:", Location = new Point(16, 84), AutoSize = true };
            var txtSubnets = new TextBox { Location = new Point(140, 80), Width = 214 };
            var hintSubnets = HelpIcon.Create(toolTip, "Одна или несколько подсетей через запятую (логическое ИЛИ).");
            hintSubnets.Location = new Point(360, 82);

            var lblScope = new Label { Text = "Scope:", Location = new Point(16, 120), AutoSize = true };
            var txtScope = new TextBox { Location = new Point(140, 116), Width = 240 };

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(216, 156), Size = new Size(80, 32) };
            var btnCreate = new Button { Text = "Создать", DialogResult = DialogResult.OK, Location = new Point(300, 156), Size = new Size(80, 32) };

            dlg.Controls.AddRange(new Control[] { lblHint, lblName, txtName, lblSubnets, txtSubnets, hintSubnets, lblScope, txtScope, btnCancel, btnCreate });
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
