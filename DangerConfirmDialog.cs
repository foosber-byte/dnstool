using System;
using System.Drawing;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>
    /// Усиленное окно подтверждения для действий, которые сложно/невозможно отменить
    /// (удаление зоны, scope, подсети, политики). В отличие от обычного MessageBox:
    /// - крупный текст на ярком фоне, чтобы взгляд точно зацепился за суть;
    /// - кнопка "Удалить" неактивна первые несколько секунд с видимым отсчётом -
    ///   не даёт кликнуть на автомате, не читая, что именно удаляется.
    /// Для менее критичных действий (например удаление одной записи) используем
    /// обычный MessageBox - не нужно нагонять драму на каждое действие.
    /// </summary>
    public static class DangerConfirmDialog
    {
        public static bool Show(string title, string what, string details, int countdownSeconds = 5)
        {
            using var dlg = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(480, 240),
                BackColor = Color.MistyRose,
                Icon = AppIcon.Current
            };

            var lblIcon = new Label
            {
                Text = "⚠",
                Font = new Font("Segoe UI", 26F, FontStyle.Bold),
                ForeColor = Color.Firebrick,
                AutoSize = true,
                Location = new Point(16, 16)
            };

            var lblWhat = new Label
            {
                Text = what,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = Color.DarkRed,
                Location = new Point(70, 18),
                Size = new Size(390, 60),
                AutoEllipsis = true
            };

            var lblDetails = new Label
            {
                Text = details,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.Black,
                Location = new Point(70, 78),
                Size = new Size(390, 90)
            };

            var btnCancel = new Button
            {
                Text = "Отмена",
                DialogResult = DialogResult.Cancel,
                Location = new Point(230, 185),
                Size = new Size(100, 32)
            };

            var btnOk = new Button
            {
                Text = $"Удалить ({countdownSeconds})",
                Enabled = false,
                DialogResult = DialogResult.OK,
                Location = new Point(340, 185),
                Size = new Size(120, 32),
                BackColor = Color.Firebrick,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };

            dlg.Controls.Add(lblIcon);
            dlg.Controls.Add(lblWhat);
            dlg.Controls.Add(lblDetails);
            dlg.Controls.Add(btnCancel);
            dlg.Controls.Add(btnOk);
            dlg.CancelButton = btnCancel;
            // Пока кнопка неактивна - Enter не должен случайно её "нажать" через AcceptButton
            dlg.AcceptButton = null;

            var remaining = countdownSeconds;
            using var timer = new Timer { Interval = 1000 };
            timer.Tick += (s, e) =>
            {
                remaining--;
                if (remaining <= 0)
                {
                    timer.Stop();
                    btnOk.Text = "Удалить";
                    btnOk.Enabled = true;
                    dlg.AcceptButton = btnOk;
                }
                else
                {
                    btnOk.Text = $"Удалить ({remaining})";
                }
            };
            timer.Start();

            var result = dlg.ShowDialog();
            timer.Stop();
            return result == DialogResult.OK;
        }
    }
}
