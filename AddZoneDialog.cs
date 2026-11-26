using System;
using System.Drawing;
using System.Net;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    public class AddZoneResult
    {
        public string Name;
        public string Type;
        /// <summary>Сеть для авто-создания обратной зоны ("192.168.10.0/24"); null - не создавать.</summary>
        public string ReverseNetworkId;
    }

    public static class AddZoneDialog
    {
        public static AddZoneResult Show()
        {
            using var dlg = new Form
            {
                Text = "Создать зону",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(420, 258),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lblName = new Label { Text = "Имя зоны:", Location = new Point(16, 20), AutoSize = true };
            var txtName = new TextBox { Location = new Point(120, 16), Width = 280 };

            var lblType = new Label { Text = "Тип:", Location = new Point(16, 58), AutoSize = true };
            var cmbType = new ComboBox { Location = new Point(120, 54), Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbType.Items.AddRange(new object[] { "AD-интегрированная (реплика: домен)", "AD-интегрированная (реплика: лес)", "Файловая (.dns на диске)" });
            cmbType.SelectedIndex = 0;

            // Авто-создание зоны обратного просмотра: DNS Server сам вычислит её имя
            // (например "192.168.10.0/24" -> "10.168.192.in-addr.arpa") - имя и .dns-файл
            // передавать не нужно, только сеть. Хранение наследуется от типа прямой зоны.
            var toolTip = new ToolTip();
            var chkReverse = new CheckBox { Text = "Также создать зону обратного просмотра для сети:", Location = new Point(16, 96), AutoSize = true };
            toolTip.SetToolTip(chkReverse,
                "Создаст Primary-зону обратного просмотра (Add-DnsServerPrimaryZone -NetworkId).\n" +
                "Имя зоны сервер вычислит сам из сети. Тип хранения - как у создаваемой прямой зоны.\n" +
                "Если такая обратная зона уже есть - получишь ошибку, прямая зона всё равно будет создана.");
            var txtNetwork = new TextBox { Location = new Point(36, 122), Width = 200, Enabled = false };
            toolTip.SetToolTip(txtNetwork,
                "Сеть в формате CIDR: \"192.168.10.0/24\", \"10.0.0.0/8\", \"2001:db8::/32\" (IPv6 тоже работает).\n" +
                "Обычно совпадает с сетью, для которой создаёшь прямую зону.");
            var lblExample = new Label { Text = "напр. 192.168.10.0/24", Location = new Point(244, 122), AutoSize = true, ForeColor = Color.DimGray };

            chkReverse.CheckedChanged += (s, e) => txtNetwork.Enabled = chkReverse.Checked;

            // Лёгкая клиентская проверка формата сети, чтобы не отправлять на сервер заведомый мусор.
            bool NetworkIdValid()
            {
                var v = txtNetwork.Text.Trim();
                var slash = v.IndexOf('/');
                if (slash <= 0) return false;
                if (!IPAddress.TryParse(v.Substring(0, slash), out var addr)) return false;
                if (!int.TryParse(v.Substring(slash + 1), out var prefix)) return false;
                return prefix >= 0 && prefix <= (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            }

            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(236, 210), Size = new Size(80, 32) };
            var btnCreate = new Button { Text = "Создать", DialogResult = DialogResult.OK, Location = new Point(320, 210), Size = new Size(80, 32) };

            dlg.Controls.AddRange(new Control[] { lblName, txtName, lblType, cmbType, chkReverse, txtNetwork, lblExample, btnCancel, btnCreate });
            dlg.AcceptButton = btnCreate;
            dlg.CancelButton = btnCancel;

            while (dlg.ShowDialog() == DialogResult.OK)
            {
                var name = txtName.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    MessageBox.Show(dlg, "Укажи имя зоны.", "Создать зону", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    continue;
                }
                if (chkReverse.Checked && !NetworkIdValid())
                {
                    MessageBox.Show(dlg, "Сеть для обратной зоны указана неверно.\nОжидается формат CIDR, например 192.168.10.0/24.",
                        "Создать зону", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    continue;
                }
                return new AddZoneResult
                {
                    Name = name,
                    Type = cmbType.Text,
                    ReverseNetworkId = chkReverse.Checked ? txtNetwork.Text.Trim() : null
                };
            }
            return null;
        }
    }
}
