using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>
    /// Небольшое окно для создания новой "папки" в дереве записей. По умолчанию - wildcard-запись
    /// "*" внутри нового поддомена (например "*.sales" -> IP): одновременно настоящая DNS-запись,
    /// отвечающая на любое имя внутри этого поддомена, и способ заставить поддомен появиться как
    /// папка в НАШЕМ дереве (папки - чисто визуальная группировка по именам записей: узел
    /// становится папкой, только если у него есть СВОИ дочерние узлы - запись "sales" сама по
    /// себе, без ничего вложенного, останется обычной строкой в общем списке, не папкой).
    ///
    /// Есть второй вариант - создать запись буквально с именем самой папки (как делает "Новый
    /// домен" в dnsmgmt.msc, если оставить имя записи пустым - "(как папка верхнего уровня)").
    /// Это НЕ сделает узел видимым как папку в нашем дереве, пока внутри неё нет ещё одной
    /// вложенной записи - явно предупреждаем об этом в самом диалоге.
    /// </summary>
    public static class CreateSubfolderDialog
    {
        public static (string Name, string Ip, bool AsWildcard) Show(string parentPathHint)
        {
            using var dlg = new Form
            {
                Text = "Создать папку (поддомен)",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(440, 268),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var toolTip = new ToolTip();

            var lblInfo = new Label
            {
                Text = $"Создаётся внутри: {parentPathHint}",
                Location = new Point(16, 14),
                Size = new Size(408, 20)
            };

            var lblName = new Label { Text = "Имя папки:", Location = new Point(16, 46), AutoSize = true };
            var txtName = new TextBox { Location = new Point(120, 42), Width = 220 };
            var hintName = HelpIcon.Create(toolTip, "Только имя нового уровня, например \"sales\" - без точек и без остальной части пути.");
            hintName.Location = new Point(366, 44);

            var lblIp = new Label { Text = "IP-адрес:", Location = new Point(16, 82), AutoSize = true };
            var txtIp = new TextBox { Location = new Point(120, 78), Width = 220, Text = "" };
            var hintIp = HelpIcon.Create(toolTip, "Адрес для первой записи внутри новой папки.");
            hintIp.Location = new Point(366, 80);

            var radioWildcard = new RadioButton
            {
                Text = "Wildcard \"*\" (рекомендуется - сразу видна как папка)",
                Checked = true,
                Location = new Point(16, 118),
                AutoSize = true
            };
            var radioLiteral = new RadioButton
            {
                Text = "Буквально как имя папки (как \"Новый домен\" в dnsmgmt.msc)",
                Location = new Point(16, 144),
                AutoSize = true
            };
            var lblWarning = new Label
            {
                Text = "При этом варианте папка НЕ появится в дереве, пока внутри нет ещё одной вложенной записи.",
                ForeColor = Color.DarkOrange,
                Location = new Point(34, 168),
                Size = new Size(390, 32),
                Visible = false
            };
            radioLiteral.CheckedChanged += (s, e) => lblWarning.Visible = radioLiteral.Checked;

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(252, 220), Size = new Size(80, 32) };
            var btnCreate = new Button { Text = "Создать", DialogResult = DialogResult.OK, Location = new Point(336, 220), Size = new Size(88, 32) };

            dlg.Controls.AddRange(new Control[]
            {
                lblInfo, lblName, txtName, hintName, lblIp, txtIp, hintIp,
                radioWildcard, radioLiteral, lblWarning, btnCancel, btnCreate
            });
            dlg.AcceptButton = btnCreate;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return (null, null, true);

            var name = txtName.Text.Trim();
            var ip = txtIp.Text.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(ip)) return (null, null, true);

            return (name, ip, radioWildcard.Checked);
        }
    }
}
