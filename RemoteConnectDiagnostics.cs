using System;
using System.Diagnostics;
using System.Drawing;
using System.Management.Automation;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>
    /// Разбор типовых причин, по которым не проходит УДАЛЁННОЕ подключение к DNS-серверу,
    /// и точечные предложения их починить. Все проверки (служба WinRM локально, ответ узла,
    /// Test-WSMan) выполняются в фоне; пользователю показывается только короткое окно с
    /// конкретным действием - запустить WinRM или добавить узел/подсеть в TrustedHosts.
    /// Сами изменения делаются во внешнем PowerShell с запросом прав администратора (UAC),
    /// а не внутри этого процесса - чтобы человек видел ровно то, что меняется, и подтверждал.
    /// </summary>
    public static class RemoteConnectDiagnostics
    {
        private class Diag
        {
            public bool WinRmRunning;
            public bool Reachable;
            public bool WsManOk;
            public string WsManError;
        }

        /// <summary>
        /// Возвращает true, если в системе что-то было изменено (запущен WinRM, дописан
        /// TrustedHosts) - тогда вызывающему коду имеет смысл повторить проверку подключения.
        /// </summary>
        public static async Task<bool> RunAsync(IWin32Window owner, string target, Action<string> log)
        {
            target = (target ?? "").Trim();
            if (target.Length == 0) return false;

            log($"Диагностика подключения к '{target}' (проверки идут в фоне)...");
            var d = await Task.Run(() => Probe(target));

            log($"  служба WinRM (локально): {(d.WinRmRunning ? "запущена" : "НЕ запущена")}");
            log($"  узел отвечает (ping): {(d.Reachable ? "да" : "нет")}");
            log($"  Test-WSMan: {(d.WsManOk ? "ок" : "ошибка" + (string.IsNullOrEmpty(d.WsManError) ? "" : $" - {Shorten(d.WsManError)}"))}");

            // 1) Локальная служба WinRM не запущена - без неё удалённые командлеты не работают вообще.
            if (!d.WinRmRunning)
            {
                var r = MessageBox.Show(owner,
                    "Служба WinRM на этом компьютере не запущена - без неё удалённое подключение невозможно.\n\n" +
                    "Запустить её (winrm quickconfig)? Откроется окно PowerShell с правами администратора, " +
                    "изменения нужно будет подтвердить.",
                    "WinRM не запущен",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return false;

                var ok = RunElevatedConsole(
                    "winrm quickconfig; Start-Service WinRM; Set-Service WinRM -StartupType Automatic");
                log(ok
                    ? "winrm quickconfig выполнен - повторяю проверку подключения."
                    : "Не удалось выполнить winrm quickconfig (возможно, отказ в запросе прав).");
                return ok;
            }

            // 2) Служба есть, но до узла не достучаться штатным способом - типично для подключения
            //    по IP или вне домена: нужен TrustedHosts на этом клиенте.
            if (!d.WsManOk)
            {
                return AskAndAddTrustedHost(owner, target, log);
            }

            log("Типовых причин на стороне транспорта не найдено - похоже, дело в правах учётной записи.");
            return false;
        }

        private static bool AskAndAddTrustedHost(IWin32Window owner, string target, Action<string> log)
        {
            var subnet = SubnetWildcard(target);

            using var dlg = new Form
            {
                Text = "Сеть до узла недоступна",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(452, 214),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lbl = new Label
            {
                Text = $"Не удалось связаться с узлом \"{target}\" по WinRM.\n\n" +
                       "Чаще всего это значит, что узел задан по IP или находится вне домена - тогда его " +
                       "нужно добавить в список доверенных хостов WinRM (TrustedHosts) на этом компьютере.",
                Location = new Point(16, 14),
                Size = new Size(420, 96)
            };

            var chkSubnet = new CheckBox
            {
                Text = subnet != null
                    ? $"Добавить всю подсеть: {subnet}"
                    : "Добавить всю подсеть - недоступно (узел задан именем, а не IP)",
                Enabled = subnet != null,
                Location = new Point(16, 116),
                AutoSize = true
            };

            var btnAdd = new Button
            {
                Text = "Добавить",
                DialogResult = DialogResult.OK,
                Location = new Point(268, 168),
                Size = new Size(84, 32),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            var btnCancel = new Button
            {
                Text = "Отмена",
                DialogResult = DialogResult.Cancel,
                Location = new Point(360, 168),
                Size = new Size(80, 32),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            dlg.Controls.AddRange(new Control[] { lbl, chkSubnet, btnAdd, btnCancel });
            dlg.AcceptButton = btnAdd;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog(owner) != DialogResult.OK) return false;

            var value = (chkSubnet.Checked && subnet != null) ? subnet : target;
            var ok = RunElevatedConsole(
                $"Set-Item WSMan:\\localhost\\Client\\TrustedHosts -Value '{Esc(value)}' -Concatenate -Force; " +
                "Get-Item WSMan:\\localhost\\Client\\TrustedHosts");
            log(ok
                ? $"В TrustedHosts добавлено: {value} - повторяю проверку подключения."
                : "Не удалось изменить TrustedHosts (возможно, отказ в запросе прав).");
            return ok;
        }

        /// <summary>Фоновые проверки - без единого окна, только читают состояние.</summary>
        private static Diag Probe(string target)
        {
            var d = new Diag();

            d.WinRmRunning = string.Equals(
                PsScalar("(Get-Service WinRM -ErrorAction SilentlyContinue).Status"),
                "Running", StringComparison.OrdinalIgnoreCase);

            d.Reachable = string.Equals(
                PsScalar($"[bool](Test-Connection -ComputerName '{Esc(target)}' -Count 1 -Quiet -ErrorAction SilentlyContinue)"),
                "True", StringComparison.OrdinalIgnoreCase);

            var w = PsScalar(
                $"try {{ Test-WSMan -ComputerName '{Esc(target)}' -ErrorAction Stop | Out-Null; 'OK' }} " +
                "catch { $_.Exception.Message }");
            d.WsManOk = string.Equals(w, "OK", StringComparison.OrdinalIgnoreCase);
            if (!d.WsManOk) d.WsManError = w;

            return d;
        }

        private static string PsScalar(string script)
        {
            try
            {
                using var ps = PowerShell.Create();
                ps.AddScript(script);
                var res = ps.Invoke();
                return res.Count > 0 ? (res[res.Count - 1]?.ToString() ?? "") : "";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Выполняет команду PowerShell во внешнем процессе с запросом прав администратора
        /// (UAC). Окно видимое и не закрывается само - человек видит вывод и подтверждает
        /// изменения (winrm quickconfig сам спрашивает "Perform these changes? [y/n]").
        /// </summary>
        private static bool RunElevatedConsole(string psCommand)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                                psCommand.Replace("\"", "\\\"") +
                                "; Write-Host ''; Read-Host 'Готово - нажми Enter, чтобы закрыть окно'\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using var p = Process.Start(psi);
                p.WaitForExit();
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false; // пользователь отклонил запрос прав в UAC
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Для IPv4 "a.b.c.d" -> "a.b.c.*" (маска целой подсети для TrustedHosts). Для имени - null.</summary>
        private static string SubnetWildcard(string host)
        {
            if (IPAddress.TryParse(host, out var ip) &&
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var p = host.Split('.');
                if (p.Length == 4) return $"{p[0]}.{p[1]}.{p[2]}.*";
            }
            return null;
        }

        private static string Esc(string s) => (s ?? "").Replace("'", "''");

        private static string Shorten(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 160 ? s : s.Substring(0, 157) + "...";
        }
    }
}
