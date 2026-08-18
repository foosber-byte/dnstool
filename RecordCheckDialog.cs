using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DnsToolWinForms
{
    /// <summary>
    /// Окно проверки DNS-записи - nslookup (через Resolve-DnsName, вывод переведён и причёсан
    /// под привычный вид консольного nslookup) + отдельно ICMP-проверка (Ping).
    ///
    /// Поле "Сервер":
    ///  - для nslookup - это DNS-сервер, у которого спрашиваем (второй аргумент nslookup);
    ///  - для Ping - это адрес источника (-S), если на этой машине несколько интерфейсов/IP
    ///    (например внутренний адрес и NAT-адрес) - позволяет проверить, как видно снаружи
    ///    с конкретного интерфейса. Это НЕ "запустить ping с другого сервера" - в Windows
    ///    у ping.exe нет линуксового -I, и такой флаг здесь физически не работает.
    /// </summary>
    public static class RecordCheckDialog
    {
        public static void Show(string currentName, string appTargetServer)
        {
            using var dlg = new Form
            {
                Text = "Проверить запись",
                FormBorderStyle = FormBorderStyle.Sizable,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(620, 460),
                MinimumSize = new Size(560, 380),
                Font = new Font("Segoe UI", 9F),
                Icon = AppIcon.Current
            };

            var lblName = new Label { Text = "Имя:", Location = new Point(16, 18), AutoSize = true };
            var txtName = new TextBox { Location = new Point(90, 14), Width = 320, Text = currentName };

            var lblServer = new Label { Text = "Сервер:", Location = new Point(16, 52), AutoSize = true };
            var txtServer = new TextBox { Location = new Point(90, 48), Width = 320 };
            var toolTip = new ToolTip();
            var lblServerHint = HelpIcon.Create(toolTip,
                (string.IsNullOrEmpty(appTargetServer)
                    ? "Для nslookup: пусто = локальный резолвер."
                    : $"Для nslookup: пусто = текущий целевой сервер ({appTargetServer}).") +
                "\nДля Ping: адрес источника (-S), если на этой машине несколько IP.");
            lblServerHint.Location = new Point(418, 50);

            var btnCheck = new Button { Text = "Проверить (nslookup)", Location = new Point(16, 90), Size = new Size(150, 30) };
            var btnPing = new Button { Text = "Ping", Location = new Point(174, 90), Size = new Size(70, 30) };
            var chkPingT = new CheckBox { Text = "-t (непрерывно)", Location = new Point(250, 95), AutoSize = true };
            var btnPingStop = new Button { Text = "Стоп", Location = new Point(400, 90), Size = new Size(70, 30), Enabled = false };

            var btnClose = new Button
            {
                Text = "Закрыть",
                DialogResult = DialogResult.Cancel,
                Location = new Point(534, 90),
                Size = new Size(70, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            var txtOutput = new TextBox
            {
                Location = new Point(16, 132),
                Size = new Size(588, 298),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            Process pingProcess = null;

            void AppendOutputLine(string line)
            {
                if (dlg.IsDisposed) return;
                dlg.BeginInvoke(new Action(() =>
                {
                    if (txtOutput.IsDisposed) return;
                    txtOutput.AppendText(line + Environment.NewLine);
                }));
            }

            // ---------- nslookup (Resolve-DnsName) ----------
            async void RunCheck(object s, EventArgs e)
            {
                var name = txtName.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    txtOutput.Text = "Укажи имя для проверки.";
                    return;
                }

                var server = txtServer.Text.Trim();
                if (string.IsNullOrEmpty(server)) server = appTargetServer; // пусто -> текущий целевой сервер приложения

                btnCheck.Enabled = false;
                txtOutput.Text = string.IsNullOrEmpty(server)
                    ? $"Проверяю '{name}' (локальный резолвер)..."
                    : $"Проверяю '{name}' на сервере '{server}'...";

                var parameters = new Dictionary<string, object> { ["Name"] = name };
                if (!string.IsNullOrEmpty(server)) parameters["Server"] = server;

                // applyGlobalComputerName: false - у Resolve-DnsName своя логика выбора сервера
                // (поле "Сервер" в этом окне), она не связана с глобальной настройкой -ComputerName
                // у командлетов DnsServer.
                var (results, log) = await Task.Run(() => DnsHelper.Invoke("Resolve-DnsName", parameters, false));

                btnCheck.Enabled = true;
                txtOutput.Text = FormatNslookupStyle(name, server, results, log);
            }

            // ---------- Ping ----------
            void RunPing(object s, EventArgs e)
            {
                var target = txtName.Text.Trim();
                if (string.IsNullOrEmpty(target))
                {
                    txtOutput.Text = "Укажи имя/адрес для проверки.";
                    return;
                }

                var source = txtServer.Text.Trim();
                var continuous = chkPingT.Checked;

                var args = new StringBuilder();
                if (!string.IsNullOrEmpty(source)) args.Append($"-S {source} ");
                args.Append(continuous ? "-t " : "-n 4 ");
                args.Append(target);

                txtOutput.Text = $"> ping {args}{Environment.NewLine}{Environment.NewLine}";
                btnPing.Enabled = false;
                btnCheck.Enabled = false;
                btnPingStop.Enabled = continuous;

                try
                {
                    pingProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "ping.exe",
                            Arguments = args.ToString(),
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            // ping.exe на русской Windows пишет в консольную кодировку CP866,
                            // а не UTF-8/ANSI - без этого вывод превращается в "кракозябры".
                            StandardOutputEncoding = Encoding.GetEncoding(866)
                        },
                        EnableRaisingEvents = true
                    };
                    pingProcess.OutputDataReceived += (ps, pe) => { if (pe.Data != null) AppendOutputLine(pe.Data); };
                    pingProcess.Exited += (ps, pe) =>
                    {
                        dlg.BeginInvoke(new Action(() =>
                        {
                            if (dlg.IsDisposed) return;
                            btnPing.Enabled = true;
                            btnCheck.Enabled = true;
                            btnPingStop.Enabled = false;
                        }));
                        pingProcess?.Dispose();
                        pingProcess = null;
                    };
                    pingProcess.Start();
                    pingProcess.BeginOutputReadLine();
                }
                catch (Exception ex)
                {
                    txtOutput.Text += $"Не удалось запустить ping.exe: {ex.Message}";
                    btnPing.Enabled = true;
                    btnCheck.Enabled = true;
                    btnPingStop.Enabled = false;
                }
            }

            btnPingStop.Click += (s, e) =>
            {
                try { pingProcess?.Kill(); } catch { /* процесс уже мог завершиться сам - не критично */ }
            };

            btnCheck.Click += RunCheck;
            btnPing.Click += RunPing;

            dlg.FormClosing += (s, e) =>
            {
                try { pingProcess?.Kill(); } catch { /* приложение закрывается - не критично */ }
            };

            dlg.Controls.AddRange(new Control[]
            {
                lblName, txtName, lblServer, txtServer, lblServerHint,
                btnCheck, btnPing, chkPingT, btnPingStop, btnClose, txtOutput
            });
            dlg.AcceptButton = btnCheck;
            dlg.CancelButton = btnClose;

            dlg.ShowDialog();
        }

        /// <summary>
        /// Приводит результат Resolve-DnsName к привычному виду консольного nslookup,
        /// но по-русски и без служебного мусора (тип .NET-объектов, CIM-поля и т.п.).
        /// </summary>
        private static string FormatNslookupStyle(string queriedName, string server, List<System.Management.Automation.PSObject> results, string log)
        {
            var sb = new StringBuilder();

            sb.AppendLine(string.IsNullOrEmpty(server)
                ? "Сервер:  (локальный резолвер)"
                : $"Сервер:  {server}");
            sb.AppendLine();

            if (!log.Contains("OK:"))
            {
                // Не гадаем с переводом произвольного текста ошибки - оставляем короткую суть
                // без служебных полей (StatusCode/ErrorSource и т.п.), это окно для понятности,
                // а не для глубокой диагностики (та по-прежнему полная в основном логе внизу).
                var shortError = log.Split('|')[0].Trim();
                sb.AppendLine($"*** Не удалось выполнить проверку для '{queriedName}'");
                sb.AppendLine(shortError);
                return sb.ToString();
            }

            if (results.Count == 0)
            {
                sb.AppendLine($"*** Сервер не смог найти запись для '{queriedName}' (имя не существует - NXDOMAIN)");
                return sb.ToString();
            }

            foreach (var r in results)
            {
                var type = r.Properties["Type"]?.Value?.ToString() ?? "?";
                var name = r.Properties["Name"]?.Value?.ToString() ?? queriedName;
                var ttl = r.Properties["TTL"]?.Value?.ToString();

                sb.AppendLine($"Имя:      {name}");

                switch (type)
                {
                    case "A":
                    case "AAAA":
                        sb.AppendLine($"Адрес:    {r.Properties["IPAddress"]?.Value}");
                        break;
                    case "CNAME":
                        sb.AppendLine($"Алиас на: {r.Properties["NameHost"]?.Value}  (тип CNAME)");
                        break;
                    case "NS":
                        sb.AppendLine($"NS-сервер: {r.Properties["NameHost"]?.Value}");
                        break;
                    case "PTR":
                        sb.AppendLine($"Имя (PTR): {r.Properties["NameHost"]?.Value}");
                        break;
                    case "MX":
                        sb.AppendLine($"Почтовый сервер: {r.Properties["NameExchange"]?.Value}  (приоритет {r.Properties["Preference"]?.Value})");
                        break;
                    case "TXT":
                        sb.AppendLine($"Текст:    {DnsHelper.FlattenPropertyValue(r.Properties["Strings"]?.Value)}");
                        break;
                    case "SOA":
                        sb.AppendLine($"SOA, основной сервер: {r.Properties["PrimaryServer"]?.Value}");
                        break;
                    default:
                        sb.AppendLine($"Тип {type}: {DnsHelper.FlattenPropertyValue(r.Properties["Data"]?.Value)}");
                        break;
                }

                if (!string.IsNullOrEmpty(ttl)) sb.AppendLine($"TTL:      {ttl} сек");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd() + Environment.NewLine;
        }
    }
}
