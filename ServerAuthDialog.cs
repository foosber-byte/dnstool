using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>
    /// Окно аутентификации на удалённом DNS-сервере - появляется автоматически, когда проверка
    /// подключения текущей Windows-учёткой не удалась (например, у неё нет прав на этом
    /// конкретном сервере). Логин/пароль → пробуем создать CimSession → зелёный OK с
    /// автозакрытием при успехе, красная ошибка с возможностью повторить при неудаче.
    /// Каждая попытка (успешная или нет) фиксируется в changes.log - пароль туда не попадает.
    /// </summary>
    public static class ServerAuthDialog
    {
        /// <summary>Возвращает true, если удалось успешно авторизоваться (тогда DnsHelper уже хранит рабочую CimSession).</summary>
        public static bool Show(string server)
        {
            using var dlg = new Form
            {
                Text = "Аутентификация на сервере",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(420, 272),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lblInfo = new Label
            {
                Text = $"Текущая учётная запись не смогла подключиться к серверу \"{server}\".\nВведи другие учётные данные для этого сервера:",
                Location = new Point(16, 14),
                Size = new Size(388, 40)
            };

            var lblLogin = new Label { Text = "Логин:", Location = new Point(16, 66), AutoSize = true };
            var txtLogin = new TextBox
            {
                Location = new Point(110, 62),
                Width = 294,
                Text = "",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var lblLoginHint = new Label
            {
                Text = "например DOMAIN\\user или user@domain.local",
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 8F, FontStyle.Italic),
                Location = new Point(110, 84),
                AutoSize = true
            };

            var lblPassword = new Label { Text = "Пароль:", Location = new Point(16, 112), AutoSize = true };
            var txtPassword = new TextBox
            {
                Location = new Point(110, 108),
                Width = 294,
                UseSystemPasswordChar = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var chkUseSsl = new CheckBox
            {
                Text = "Подключаться через HTTPS (WinRM, порт 5986)",
                Location = new Point(110, 140),
                AutoSize = true
            };
            var lblSslHint = new Label
            {
                Text = "требует HTTPS-листенер WinRM и валидный сертификат на сервере",
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 8F, FontStyle.Italic),
                Location = new Point(110, 160),
                AutoSize = true
            };

            var progress = new ProgressBar
            {
                Location = new Point(16, 188),
                Size = new Size(388, 18),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Visible = false
            };

            var lblStatus = new Label
            {
                Location = new Point(16, 188),
                Size = new Size(388, 40),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Text = ""
            };

            var btnCancel = new Button
            {
                Text = "Отмена",
                DialogResult = DialogResult.Cancel,
                Location = new Point(232, 228),
                Size = new Size(80, 32),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            var btnLogin = new Button
            {
                Text = "Войти",
                Location = new Point(324, 228),
                Size = new Size(80, 32),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            var success = false;

            async void DoLogin(object s, EventArgs e)
            {
                var login = txtLogin.Text.Trim();

                if (string.IsNullOrEmpty(login) || txtPassword.Text.Length == 0)
                {
                    lblStatus.ForeColor = Color.Firebrick;
                    lblStatus.Text = "Заполни логин и пароль.";
                    return;
                }

                // Строим пароль СРАЗУ как SecureString посимвольно и сразу же чистим текстовое
                // поле - не держим пароль в открытом виде (ни как string, ни на экране) дольше,
                // чем необходимо для одного этого прохода.
                var securePassword = new System.Security.SecureString();
                foreach (var c in txtPassword.Text) securePassword.AppendChar(c);
                securePassword.MakeReadOnly();
                txtPassword.Clear();

                var useSsl = chkUseSsl.Checked;

                btnLogin.Enabled = false;
                btnCancel.Enabled = false;
                txtLogin.Enabled = false;
                txtPassword.Enabled = false;
                chkUseSsl.Enabled = false;
                lblStatus.Text = "";
                progress.Visible = true;

                var (ok, error) = await Task.Run(() => DnsHelper.TryAuthenticate(server, login, securePassword, useSsl));

                progress.Visible = false;

                FileLogger.LogChange("AUTH", server,
                    $"пользователь ввёл логин '{login}'" + (useSsl ? " (HTTPS)" : ""), ok, ok ? null : error);

                if (ok)
                {
                    lblStatus.ForeColor = Color.SeaGreen;
                    lblStatus.Text = "OK: подключение успешно.";
                    success = true;
                    await Task.Delay(900); // дать секунду увидеть зелёный статус перед закрытием
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                }
                else
                {
                    lblStatus.ForeColor = Color.Firebrick;
                    lblStatus.Text = "Ошибка логина или пароля - проверьте права на сервере." +
                                      (string.IsNullOrEmpty(error) ? "" : $"\n({error})");
                    btnLogin.Enabled = true;
                    btnCancel.Enabled = true;
                    txtLogin.Enabled = true;
                    txtPassword.Enabled = true;
                    chkUseSsl.Enabled = true;
                }
            }

            btnLogin.Click += DoLogin;

            dlg.Controls.AddRange(new Control[]
            {
                lblInfo, lblLogin, txtLogin, lblLoginHint, lblPassword, txtPassword,
                chkUseSsl, lblSslHint, progress, lblStatus, btnCancel, btnLogin
            });
            dlg.AcceptButton = btnLogin;
            dlg.CancelButton = btnCancel;

            dlg.ShowDialog();
            return success;
        }
    }
}
