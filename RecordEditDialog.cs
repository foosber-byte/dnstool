using System;
using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>Результат диалога редактирования записи - null, если нажали "Отмена".</summary>
    public class RecordEditResult
    {
        public string Type;
        public string Name;
        public string Value;
        public string Priority;
        public string Weight;
        public string Port;
    }

    /// <summary>
    /// Окно редактирования DNS-записи (по двойному клику или из контекстного меню).
    /// В модуле DnsServer нет надёжной "команды переименования на месте" для всех типов
    /// записей сразу, поэтому под капотом это всегда пересоздание: сначала добавляется
    /// новая запись с изменёнными значениями, и только при успехе удаляется старая
    /// (см. MainForm.EditSelectedRecordAsync) - так не теряем данные, если что-то пойдёт не так.
    /// </summary>
    public static class RecordEditDialog
    {
        public static RecordEditResult Show(string currentType, string currentName, string currentValue,
            string currentPriority, string currentWeight, string currentPort)
        {
            using var dlg = new Form
            {
                Text = "Изменить запись",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(440, 330),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lblNote = new Label
            {
                Text = "Запись будет пересоздана с новыми значениями (сначала добавляется новая, потом удаляется старая).",
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
                Location = new Point(16, 12),
                Size = new Size(408, 40)
            };

            var lblType = new Label { Text = "Тип записи:", Location = new Point(16, 62), AutoSize = true };
            var cmbType = new ComboBox { Location = new Point(140, 58), Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbType.Items.AddRange(new object[] { "A", "AAAA", "CNAME", "PTR", "TXT", "SRV" });
            cmbType.SelectedItem = cmbType.Items.Contains(currentType) ? currentType : "A";

            var lblName = new Label { Text = "Имя:", Location = new Point(16, 100), AutoSize = true };
            var txtName = new TextBox { Location = new Point(140, 96), Width = 280, Text = currentName };

            var lblValue = new Label { Text = "Значение:", Location = new Point(16, 138), AutoSize = true };
            var txtValue = new TextBox { Location = new Point(140, 134), Width = 280, Text = currentValue };

            var lblSrvHint = new Label
            {
                Text = "Ниже - только для SRV:",
                ForeColor = Color.DimGray,
                Location = new Point(16, 176),
                AutoSize = true
            };

            var lblPriority = new Label { Text = "Priority:", Location = new Point(16, 204), AutoSize = true };
            var txtPriority = new TextBox { Location = new Point(90, 200), Width = 60, Text = string.IsNullOrEmpty(currentPriority) ? "10" : currentPriority };

            var lblWeight = new Label { Text = "Weight:", Location = new Point(160, 204), AutoSize = true };
            var txtWeight = new TextBox { Location = new Point(230, 200), Width = 60, Text = string.IsNullOrEmpty(currentWeight) ? "10" : currentWeight };

            var lblPort = new Label { Text = "Port:", Location = new Point(300, 204), AutoSize = true };
            var txtPort = new TextBox { Location = new Point(350, 200), Width = 70, Text = string.IsNullOrEmpty(currentPort) ? "443" : currentPort };

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(240, 260), Size = new Size(90, 32) };
            var btnSave = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, Location = new Point(336, 260), Size = new Size(90, 32) };

            dlg.Controls.AddRange(new Control[]
            {
                lblNote, lblType, cmbType, lblName, txtName, lblValue, txtValue,
                lblSrvHint, lblPriority, txtPriority, lblWeight, txtWeight, lblPort, txtPort,
                btnCancel, btnSave
            });
            dlg.AcceptButton = btnSave;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog() != DialogResult.OK) return null;

            return new RecordEditResult
            {
                Type = cmbType.Text,
                Name = txtName.Text.Trim(),
                Value = txtValue.Text.Trim(),
                Priority = txtPriority.Text.Trim(),
                Weight = txtWeight.Text.Trim(),
                Port = txtPort.Text.Trim()
            };
        }
    }
}
