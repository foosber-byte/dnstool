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
    ///
    /// Транспорт - обычный WinRM (Kerberos, либо NTLM через TrustedHosts на клиенте), без
    /// HTTPS и без пропуска проверки сертификата - эти два варианта были в приложении раньше
    /// (v2.1.0), но убраны в v2.2.1: на практике HTTPS-путь давал непредсказуемые ошибки
    /// (сертификат не подходит для IP-адреса, либо сервер отвечал "Отказано в доступе" даже
    /// после успешного TLS-рукопожатия - конфигурация HTTPS-листенера WinRM на конкретном
    /// сервере, а не то, что можно починить в этом приложении), а сам флаг "пропустить проверку
    /// сертификата" по своей сути ослабляет защиту транспорта и закономерно вызывает вопросы
    /// при разборе кода службой ИБ. Обычный путь (TrustedHosts + Kerberos/NTLM) - это то же
    /// самое, что использует стандартный `Enter-PSSession`/`dnsmgmt.msc` при удалённом
    /// управлении, ничего сверх штатных возможностей Windows.
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
                ClientSize = new Size(420, 260),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lblInfo = new Label
            {
                Text = $"Текущая учётная запись не смогла подключиться к серверу \"{server}\".\nВведи другие учётные данные для этого сервера:",
                Location = new Point(16, 14),
                Size = new Size(388, 40)
            };

            var toolTip = new ToolTip();

            var lblLogin = new Label { Text = "Логин:", Location = new Point(16, 58), AutoSize = true };
            var txtLogin = new TextBox
            {
                Location = new Point(110, 54),
                Width = 270,
                Text = "",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            var hintLogin = HelpIcon.Create(toolTip, "Например: DOMAIN\\user или user@domain.local");
            hintLogin.Location = new Point(386, 55);
            hintLogin.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            var lblPassword = new Label { Text = "Пароль:", Location = new Point(16, 90), AutoSize = true };
            var txtPassword = new TextBox
            {
                Location = new Point(110, 86),
                Width = 294,
                UseSystemPasswordChar = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var progress = new ProgressBar
            {
                Location = new Point(16, 128),
                Size = new Size(388, 18),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Visible = false
            };

            var txtStatus = new TextBox
            {
                Location = new Point(16, 128),
                Size = new Size(388, 92),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.DimGray,
                Text = ""
            };

            var btnCancel = new Button
            {
                Text = "Отмена",
                DialogResult = DialogResult.Cancel,
                Location = new Point(232, 218),
                Size = new Size(80, 32),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            var btnLogin = new Button
            {
                Text = "Войти",
                Location = new Point(324, 218),
                Size = new Size(80, 32),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            var success = false;

            async void DoLogin(object s, EventArgs e)
            {
                var login = txtLogin.Text.Trim();

                if (string.IsNullOrEmpty(login) || txtPassword.Text.Length == 0)
                {
                    txtStatus.ForeColor = Color.Firebrick;
                    txtStatus.Text = "Заполни логин и пароль.";
                    return;
                }

                // Строим пароль СРАЗУ как SecureString посимвольно и сразу же чистим текстовое
                // поле - не держим пароль в открытом виде (ни как string, ни на экране) дольше,
                // чем необходимо для одного этого прохода.
                var securePassword = new System.Security.SecureString();
                foreach (var c in txtPassword.Text) securePassword.AppendChar(c);
                securePassword.MakeReadOnly();
                txtPassword.Clear();

                btnLogin.Enabled = false;
                btnCancel.Enabled = false;
                txtLogin.Enabled = false;
                txtPassword.Enabled = false;
                txtStatus.Text = "";
                progress.Visible = true;

                var (ok, error) = await Task.Run(() => DnsHelper.TryAuthenticate(server, login, securePassword));

                progress.Visible = false;

                FileLogger.LogChange("AUTH", server, $"пользователь ввёл логин '{login}'", ok, ok ? null : error);

                if (ok)
                {
                    txtStatus.ForeColor = Color.SeaGreen;
                    txtStatus.Text = "OK: подключение успешно.";
                    success = true;
                    await Task.Delay(900); // дать секунду увидеть зелёный статус перед закрытием
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                }
                else
                {
                    txtStatus.ForeColor = Color.Firebrick;
                    txtStatus.Text = "Не удалось подключиться." +
                                      (string.IsNullOrEmpty(error) ? " Причина не определена." : $"\n{error}");
                    btnLogin.Enabled = true;
                    btnCancel.Enabled = true;
                    txtLogin.Enabled = true;
                    txtPassword.Enabled = true;
                }
            }

            btnLogin.Click += DoLogin;

            dlg.Controls.AddRange(new Control[]
            {
                lblInfo, lblLogin, txtLogin, hintLogin, lblPassword, txtPassword,
                progress, txtStatus, btnCancel, btnLogin
            });
            dlg.AcceptButton = btnLogin;
            dlg.CancelButton = btnCancel;

            dlg.ShowDialog();
            return success;
        }
    }
}
