using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>
    /// Окно "О программе" - единственное место в приложении, где баннер-логотип
    /// показывается целиком (в остальном интерфейсе - только маленький значок,
    /// чтобы не выбиваться из утилитарного вида остальных окон).
    /// </summary>
    public static class AboutDialog
    {
        public static void Show()
        {
            using var dlg = new Form
            {
                Text = "О программе",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(480, 330),
                BackColor = Color.White,
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var y = 24;

            // Баннер - если файл banner.png лежит рядом с exe, показываем его целиком.
            // Если файла нет (например, кто-то собрал проект без него) - тихо пропускаем,
            // ничего не ломаем, просто баннера не будет.
            var bannerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "banner.png");
            if (File.Exists(bannerPath))
            {
                try
                {
                    using var original = Image.FromFile(bannerPath);
                    var displayWidth = 400;
                    var displayHeight = (int)(original.Height * (displayWidth / (float)original.Width));

                    var pic = new PictureBox
                    {
                        Image = new Bitmap(original, new Size(displayWidth, displayHeight)),
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Location = new Point((480 - displayWidth) / 2, y),
                        Size = new Size(displayWidth, displayHeight)
                    };
                    dlg.Controls.Add(pic);
                    y += displayHeight + 24;
                }
                catch { /* повреждённый файл баннера - не критично, просто пропускаем */ }
            }

            void AddCenteredLabel(string text, Font font, Color color, int height)
            {
                var lbl = new Label
                {
                    Text = text,
                    Font = font,
                    ForeColor = color,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Location = new Point(0, y),
                    Size = new Size(480, height)
                };
                dlg.Controls.Add(lbl);
                y += height;
            }

            AddCenteredLabel("Версия: см. заголовок окна (v2.0.1)", new Font("Segoe UI", 9F), Color.DimGray, 22);
            AddCenteredLabel("Автор: Kuzanov.e, 2026", new Font("Segoe UI", 9F), Color.DimGray, 22);
            AddCenteredLabel("Лицензия: MIT - свободное использование, изменение, форк", new Font("Segoe UI", 8.5F), Color.Gray, 20);

            var linkGitHub = new LinkLabel
            {
                Text = "GitHub-репозиторий",
                Location = new Point(0, y + 6),
                Size = new Size(480, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                LinkColor = Color.SteelBlue
            };
            linkGitHub.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("https://github.com/foosber-byte/dnstool") { UseShellExecute = true }); }
                catch { /* нет доступа в интернет с этого сервера - не критично */ }
            };
            dlg.Controls.Add(linkGitHub);

            var btnClose = new Button
            {
                Text = "Закрыть",
                DialogResult = DialogResult.OK,
                Size = new Size(90, 32),
                Location = new Point((480 - 90) / 2, 290)
            };
            dlg.Controls.Add(btnClose);
            dlg.AcceptButton = btnClose;
            dlg.CancelButton = btnClose;

            dlg.ShowDialog();
        }
    }
}
