using System;
using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>Режим импорта подсетей из файла.</summary>
    public enum SubnetImportMode
    {
        /// <summary>Удалить все текущие подсети и создать те, что в файле (по умолчанию).</summary>
        Replace,
        /// <summary>Добавить подсети из файла к уже существующим (дубликаты по имени пропускаются).</summary>
        Append
    }

    public static class ImportSubnetsDialog
    {
        /// <summary>
        /// Возвращает выбранный режим импорта или null, если пользователь отменил.
        /// Две галки взаимоисключающие: "Заменить текущие сети" (включена по умолчанию)
        /// и "Добавить к существующим".
        /// </summary>
        public static SubnetImportMode? Show()
        {
            using var dlg = new Form
            {
                Text = "Импорт подсетей",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(380, 150),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var chkReplace = new CheckBox
            {
                Text = "Заменить текущие сети",
                Checked = true,
                Location = new Point(16, 20),
                AutoSize = true
            };
            var chkAppend = new CheckBox
            {
                Text = "Добавить к существующим",
                Location = new Point(16, 52),
                AutoSize = true
            };

            // Галки ведут себя как переключатель: включение одной снимает другую,
            // но обе могут быть выключены - тогда импорт невозможен.
            chkReplace.CheckedChanged += (s, e) => { if (chkReplace.Checked) chkAppend.Checked = false; };
            chkAppend.CheckedChanged += (s, e) => { if (chkAppend.Checked) chkReplace.Checked = false; };

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(196, 100), Size = new Size(80, 32) };
            var btnOk = new Button { Text = "Далее", DialogResult = DialogResult.OK, Location = new Point(280, 100), Size = new Size(80, 32) };

            dlg.Controls.AddRange(new Control[] { chkReplace, chkAppend, btnCancel, btnOk });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return null;
            if (chkReplace.Checked) return SubnetImportMode.Replace;
            if (chkAppend.Checked) return SubnetImportMode.Append;

            MessageBox.Show("Выбери режим импорта: замена текущих сетей или добавление к существующим.",
                "Импорт подсетей", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
    }
}
