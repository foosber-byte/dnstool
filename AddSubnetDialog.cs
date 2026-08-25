using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public static class AddSubnetDialog
    {
        public static (string Name, string Cidr) Show()
        {
            using var dlg = new Form
            {
                Text = "Создать подсеть",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(380, 160),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var toolTip = new ToolTip();

            var lblName = new Label { Text = "Имя подсети:", Location = new Point(16, 20), AutoSize = true };
            var txtName = new TextBox { Location = new Point(130, 16), Width = 200 };

            var lblCidr = new Label { Text = "CIDR:", Location = new Point(16, 58), AutoSize = true };
            var txtCidr = new TextBox { Location = new Point(130, 54), Width = 200 };
            var hintCidr = HelpIcon.Create(toolTip, "Например 10.0.1.0/24");
            hintCidr.Location = new Point(338, 56);

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(196, 100), Size = new Size(80, 32) };
            var btnCreate = new Button { Text = "Создать", DialogResult = DialogResult.OK, Location = new Point(280, 100), Size = new Size(80, 32) };

            dlg.Controls.AddRange(new Control[] { lblName, txtName, lblCidr, txtCidr, hintCidr, btnCancel, btnCreate });
            dlg.AcceptButton = btnCreate;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return (null, null);
            var name = txtName.Text.Trim();
            var cidr = txtCidr.Text.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cidr)) return (null, null);
            return (name, cidr);
        }
    }
}
