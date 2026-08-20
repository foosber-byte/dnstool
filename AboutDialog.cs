using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
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

            AddCenteredLabel($"Версия: v{AppVersion.Current}", new Font("Segoe UI", 9F), Color.DimGray, 22);
            AddCenteredLabel("Автор: foosber, 2026", new Font("Segoe UI", 9F), Color.DimGray, 22);
            AddCenteredLabel("Лицензия: MIT", new Font("Segoe UI", 8.5F), Color.Gray, 20);

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
            y += 32;

            var btnUpdate = new Button
            {
                Text = "Проверить обновления",
                Location = new Point((480 - 170) / 2, y),
                Size = new Size(170, 30)
            };

            var lblUpdateStatus = new Label
            {
                Location = new Point(24, y + 36),
                Size = new Size(432, 40),
                TextAlign = ContentAlignment.TopCenter,
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.DimGray,
                Text = ""
            };

            async void CheckForUpdate(object s, EventArgs e)
            {
                btnUpdate.Enabled = false;
                lblUpdateStatus.ForeColor = Color.DimGray;
                lblUpdateStatus.Text = "Проверяю GitHub...";

                var (success, error, info) = await UpdateChecker.CheckLatestAsync();

                if (!success)
                {
                    lblUpdateStatus.ForeColor = Color.Firebrick;
                    lblUpdateStatus.Text = error;
                    btnUpdate.Enabled = true;
                    return;
                }

                if (!UpdateChecker.IsNewer(info.Version, AppVersion.Current))
                {
                    lblUpdateStatus.ForeColor = Color.SeaGreen;
                    lblUpdateStatus.Text = $"У тебя уже последняя версия (v{AppVersion.Current}).";
                    btnUpdate.Enabled = true;
                    return;
                }

                var confirm = MessageBox.Show(
                    $"Доступна новая версия: v{info.Version} (у тебя v{AppVersion.Current}).\n\n" +
                    "Скачать и установить? Приложение закроется и перезапустится само.\n" +
                    "changes.log и settings.ini не будут тронуты.",
                    "Доступно обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                if (confirm != DialogResult.Yes)
                {
                    lblUpdateStatus.Text = "";
                    btnUpdate.Enabled = true;
                    return;
                }

                try
                {
                    lblUpdateStatus.ForeColor = Color.DimGray;
                    lblUpdateStatus.Text = "Скачиваю и распаковываю обновление...";

                    var updaterScript = await UpdateChecker.DownloadAndPrepareUpdateAsync(info.DownloadUrl);

                    FileLogger.LogChange("UPDATE", "GitHub", $"Скачано обновление до v{info.Version}, перезапуск...", true);

                    lblUpdateStatus.ForeColor = Color.SeaGreen;
                    lblUpdateStatus.Text = "Готово. Приложение сейчас перезапустится...";
                    await Task.Delay(800);

                    UpdateChecker.LaunchUpdaterAndExit(updaterScript);
                }
                catch (Exception ex)
                {
                    lblUpdateStatus.ForeColor = Color.Firebrick;
                    lblUpdateStatus.Text = "Ошибка обновления: " + ex.Message;
                    FileLogger.LogChange("UPDATE", "GitHub", $"Попытка обновления до v{info.Version}", false, ex.Message);
                    btnUpdate.Enabled = true;
                }
            }

            btnUpdate.Click += CheckForUpdate;
            dlg.Controls.Add(btnUpdate);
            dlg.Controls.Add(lblUpdateStatus);
            y += 84;

            var btnClose = new Button
            {
                Text = "Закрыть",
                DialogResult = DialogResult.OK,
                Size = new Size(90, 32),
                Location = new Point((480 - 90) / 2, y)
            };
            dlg.Controls.Add(btnClose);
            dlg.AcceptButton = btnClose;
            dlg.CancelButton = btnClose;

            dlg.ClientSize = new Size(480, y + 50);
            dlg.ShowDialog();
        }
    }
}
