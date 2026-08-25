using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>
    /// Небольшое окно для создания новой "папки" в дереве записей - технически это wildcard-запись
    /// "*" внутри нового поддомена (например "*.sales" -> IP), которая одновременно
    /// создаёт реальную DNS-запись, отвечающую на любое имя внутри этого поддомена,
    /// и заставляет поддомен появиться как папка в дереве (папки - чисто визуальная
    /// группировка по именам записей, без хотя бы одной записи внутри папка не существует).
    /// </summary>
    public static class CreateSubfolderDialog
    {
        public static (string Name, string Ip) Show(string parentPathHint)
        {
            using var dlg = new Form
            {
                Text = "Создать папку (поддомен)",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(420, 220),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var toolTip = new ToolTip();

            var lblInfo = new Label
            {
                Text = $"Создаётся внутри: {parentPathHint}\n\nПапка технически - это wildcard-запись \"*\" внутри\nнового поддомена. Без неё папка не появится в дереве.",
                Location = new Point(16, 14),
                Size = new Size(388, 56)
            };

            var lblName = new Label { Text = "Имя папки:", Location = new Point(16, 82), AutoSize = true };
            var txtName = new TextBox { Location = new Point(120, 78), Width = 220 };
            var hintName = HelpIcon.Create(toolTip, "Только имя нового уровня, например \"sales\" - без точек и без остальной части пути.");
            hintName.Location = new Point(346, 80);

            var lblIp = new Label { Text = "IP-адрес:", Location = new Point(16, 118), AutoSize = true };
            var txtIp = new TextBox { Location = new Point(120, 114), Width = 220, Text = "" };
            var hintIp = HelpIcon.Create(toolTip, "Адрес для записи \"*\" внутри новой папки - на что будет отвечать любое имя в этом поддомене.");
            hintIp.Location = new Point(346, 116);

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(232, 168), Size = new Size(80, 32) };
            var btnCreate = new Button { Text = "Создать", DialogResult = DialogResult.OK, Location = new Point(316, 168), Size = new Size(88, 32) };

            dlg.Controls.AddRange(new Control[] { lblInfo, lblName, txtName, hintName, lblIp, txtIp, hintIp, btnCancel, btnCreate });
            dlg.AcceptButton = btnCreate;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return (null, null);

            var name = txtName.Text.Trim();
            var ip = txtIp.Text.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(ip)) return (null, null);

            return (name, ip);
        }
    }
}
