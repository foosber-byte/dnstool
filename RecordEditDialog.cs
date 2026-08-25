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
            string currentPriority, string currentWeight, string currentPort, bool isNew = false)
        {
            using var dlg = new Form
            {
                Text = isNew ? "Добавить запись" : "Изменить запись",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(440, 270),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var toolTip = new ToolTip();

            var lblType = new Label { Text = "Тип записи:", Location = new Point(16, 24), AutoSize = true };
            var cmbType = new ComboBox { Location = new Point(140, 20), Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbType.Items.AddRange(new object[] { "A", "AAAA", "CNAME", "PTR", "NS", "MX", "TXT", "SRV" });
            cmbType.SelectedItem = cmbType.Items.Contains(currentType) ? currentType : "A";
            var hintNote = HelpIcon.Create(toolTip, isNew
                ? "Запись добавляется в scope/папку, которая сейчас выбрана в дереве слева."
                : "Запись будет пересоздана с новыми значениями: сначала добавляется новая, " +
                  "и только при успехе удаляется старая - исходная запись не теряется при сбое.");
            hintNote.Location = new Point(268, 22);

            var lblName = new Label { Text = "Имя:", Location = new Point(16, 62), AutoSize = true };
            var txtName = new TextBox { Location = new Point(140, 58), Width = 280, Text = currentName };

            var lblValue = new Label { Text = "Значение:", Location = new Point(16, 100), AutoSize = true };
            var txtValue = new TextBox { Location = new Point(140, 96), Width = 280, Text = currentValue };

            var lblPriority = new Label { Text = "Priority:", Location = new Point(16, 138), AutoSize = true };
            var txtPriority = new TextBox { Location = new Point(90, 134), Width = 60, Text = string.IsNullOrEmpty(currentPriority) ? "10" : currentPriority };

            var lblWeight = new Label { Text = "Weight:", Location = new Point(160, 138), AutoSize = true };
            var txtWeight = new TextBox { Location = new Point(230, 134), Width = 60, Text = string.IsNullOrEmpty(currentWeight) ? "10" : currentWeight };

            var lblPort = new Label { Text = "Port:", Location = new Point(300, 138), AutoSize = true };
            var txtPort = new TextBox { Location = new Point(350, 134), Width = 70, Text = string.IsNullOrEmpty(currentPort) ? "443" : currentPort };

            var hintSrv = HelpIcon.Create(toolTip,
                "Для SRV используются все три поля. Для MX - только Priority (это Preference); " +
                "Weight и Port игнорируются. Для остальных типов записей эти поля не используются.");
            hintSrv.Location = new Point(16, 160);

            // Поля Priority/Weight/Port нужны только для SRV и MX - показываем их только тогда,
            // когда выбран подходящий тип, а не всегда (для A/AAAA/CNAME и т.п. они бы просто
            // занимали место, ничего не делая).
            void UpdateFieldVisibility()
            {
                var type = cmbType.Text;
                var isSrv = type == "SRV";
                var isMx = type == "MX";
                var needsAny = isSrv || isMx;

                lblPriority.Visible = needsAny;
                txtPriority.Visible = needsAny;
                lblPriority.Text = isMx ? "Preference:" : "Priority:";

                lblWeight.Visible = isSrv;
                txtWeight.Visible = isSrv;

                lblPort.Visible = isSrv;
                txtPort.Visible = isSrv;

                hintSrv.Visible = needsAny;
            }

            cmbType.SelectedIndexChanged += (s, e) => UpdateFieldVisibility();
            UpdateFieldVisibility(); // начальное состояние - сразу после создания всех полей выше

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(240, 200), Size = new Size(90, 32) };
            var btnSave = new Button { Text = isNew ? "Добавить" : "Сохранить", DialogResult = DialogResult.OK, Location = new Point(336, 200), Size = new Size(90, 32) };

            dlg.Controls.AddRange(new Control[]
            {
                lblType, cmbType, hintNote, lblName, txtName, lblValue, txtValue,
                lblPriority, txtPriority, lblWeight, txtWeight, lblPort, txtPort, hintSrv,
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
