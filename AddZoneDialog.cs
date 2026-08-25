using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public static class AddZoneDialog
    {
        public static (string Name, string Type) Show()
        {
            using var dlg = new Form
            {
                Text = "Создать зону",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(380, 160),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lblName = new Label { Text = "Имя зоны:", Location = new Point(16, 20), AutoSize = true };
            var txtName = new TextBox { Location = new Point(120, 16), Width = 240 };

            var lblType = new Label { Text = "Тип:", Location = new Point(16, 58), AutoSize = true };
            var cmbType = new ComboBox { Location = new Point(120, 54), Width = 240, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbType.Items.AddRange(new object[] { "AD-интегрированная (реплика: домен)", "AD-интегрированная (реплика: лес)", "Файловая (.dns на диске)" });
            cmbType.SelectedIndex = 0;

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(196, 100), Size = new Size(80, 32) };
            var btnCreate = new Button { Text = "Создать", DialogResult = DialogResult.OK, Location = new Point(280, 100), Size = new Size(80, 32) };

            dlg.Controls.AddRange(new Control[] { lblName, txtName, lblType, cmbType, btnCancel, btnCreate });
            dlg.AcceptButton = btnCreate;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return (null, null);
            var name = txtName.Text.Trim();
            if (string.IsNullOrEmpty(name)) return (null, null);
            return (name, cmbType.Text);
        }
    }
}
