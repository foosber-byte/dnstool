using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Text;

namespace DnsToolWinForms
{
    /// <summary>
    /// Тонкая обёртка над модулем PowerShell "DnsServer".
    /// Параметры передаются через AddParameter, а не склейкой строк -
    /// так не страшны кавычки/спецсимволы в том, что введёт пользователь.
    /// </summary>
    public static class DnsHelper
    {
        /// <summary>
        /// Если задано - каждый вызов командлета автоматически получает -ComputerName с этим
        /// значением, и вся операция физически выполняется на указанном удалённом DNS-сервере
        /// (через WinRM, стандартный механизм для CIM-командлетов модуля DnsServer). Пусто = локальный
        /// компьютер (поведение по умолчанию, как было раньше).
        /// </summary>
        public static string ComputerName { get; set; } = "";

        // Активная CimSession с явными кредами (если авторизовались вручную через ServerAuthDialog)
        // и сервер, для которого она создана. Пока эти два совпадают с текущим ComputerName -
        // используем именно её вместо -ComputerName (иначе PowerShell снова попробует текущую
        // Windows-учётку и получит тот же отказ в правах).
        private static object _activeCimSession;
        private static string _activeCimSessionComputer;

        /// <summary>
        /// Пробует создать CimSession с явно указанными логином/паролем (New-CimSession -Credential).
        /// У командлетов модуля DnsServer нет собственного параметра -Credential - единственный
        /// официальный способ подключиться под другой учёткой - через уже готовую CimSession.
        /// При успехе сессия сохраняется и автоматически используется во всех дальнейших вызовах
        /// Invoke() для этого же сервера, пока не сменится целевой сервер или не завершится приложение.
        /// </summary>
        /// <param name="password">
        /// Пароль уже в виде SecureString - строится сразу в UI посимвольно, минуя лишний
        /// plain-string на этом уровне (меньше времени пароль существует в памяти как обычная строка).
        /// </param>
        /// <param name="useSsl">
        /// Если true - WinRM идёт через HTTPS (порт 5986) вместо HTTP (5985), полное TLS-шифрование
        /// транспорта поверх и так уже зашифрованных Kerberos/NTLM-сообщений. Требует валидного
        /// сертификата на целевом сервере и включённого HTTPS-листенера WinRM.
        /// </param>
        public static (bool Success, string Error) TryAuthenticate(string computerName, string username, System.Security.SecureString password, bool useSsl)
        {
            try
            {
                var credential = new PSCredential(username, password);

                using var ps = PowerShell.Create();
                ps.AddCommand("New-CimSession")
                  .AddParameter("ComputerName", computerName)
                  .AddParameter("Credential", credential);
                if (useSsl) ps.AddParameter("UseSSL");
                ps.AddParameter("ErrorAction", "Stop");

                var results = ps.Invoke();

                if (ps.HadErrors)
                {
                    var err = ps.Streams.Error.FirstOrDefault();
                    var msg = err != null ? DescribeException(err.Exception) : "неизвестная ошибка при создании сессии";
                    return (false, msg);
                }

                var session = results.FirstOrDefault()?.BaseObject;
                if (session == null)
                    return (false, "New-CimSession не вернул объект сессии");

                DisposeActiveCimSession(); // закрываем предыдущую, если была - не плодим висящие подключения
                _activeCimSession = session;
                _activeCimSessionComputer = computerName;
                return (true, null);
            }
            catch (System.Exception ex)
            {
                return (false, DescribeException(ex));
            }
        }

        /// <summary>Закрывает и забывает активную CimSession, если она есть.</summary>
        public static void DisposeActiveCimSession()
        {
            if (_activeCimSession is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { /* сессия уже могла отвалиться сама - не критично */ }
            }
            _activeCimSession = null;
            _activeCimSessionComputer = null;
        }

        /// <summary>
        /// Вызывать при смене целевого сервера в UI - если активная CimSession была для ДРУГОГО
        /// сервера, она больше не актуальна и её надо закрыть (иначе следующий вызов Invoke()
        /// попробует использовать чужую сессию не по адресу).
        /// </summary>
        public static void InvalidateCimSessionIfServerChanged(string newComputerName)
        {
            var normalized = (newComputerName ?? "").Trim();
            if (_activeCimSession != null &&
                !string.Equals(_activeCimSessionComputer, normalized, StringComparison.OrdinalIgnoreCase))
            {
                DisposeActiveCimSession();
            }
        }

        /// <summary>
        /// Выполняет один командлет с параметрами. Возвращает (результаты, текстовый лог для блока вывода).
        /// </summary>
        /// <param name="applyGlobalComputerName">
        /// Если true (по умолчанию) - автоматически подставляет -ComputerName из ComputerName выше.
        /// Для Resolve-DnsName (проверка записи) это не нужно - там сервер для запроса выбирается
        /// отдельным полем в самом диалоге проверки, а не глобальной настройкой "Целевой сервер".
        /// </param>
        public static (List<PSObject> Results, string Log) Invoke(string cmdlet, Dictionary<string, object> parameters = null, bool applyGlobalComputerName = true)
        {
            using var ps = PowerShell.Create();
            ps.AddCommand(cmdlet);

            if (parameters != null)
            {
                foreach (var kv in parameters)
                {
                    if (kv.Value is bool b)
                    {
                        // switch-параметры (например -Force) добавляем только если true
                        if (b) ps.AddParameter(kv.Key);
                    }
                    else
                    {
                        ps.AddParameter(kv.Key, kv.Value);
                    }
                }
            }

            if (applyGlobalComputerName && !string.IsNullOrWhiteSpace(ComputerName) &&
                (parameters == null || !parameters.ContainsKey("ComputerName")))
            {
                if (_activeCimSession != null &&
                    string.Equals(_activeCimSessionComputer, ComputerName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    // Есть готовая сессия с явными кредами для именно этого сервера - используем её
                    // вместо -ComputerName, иначе PowerShell снова попробует текущую Windows-учётку.
                    ps.AddParameter("CimSession", _activeCimSession);
                }
                else
                {
                    ps.AddParameter("ComputerName", ComputerName.Trim());
                }
            }

            List<PSObject> results;
            var log = new StringBuilder();

            try
            {
                results = ps.Invoke().ToList();
            }
            catch (System.Exception ex)
            {
                log.AppendLine($"ИСКЛЮЧЕНИЕ при вызове {cmdlet}: {DescribeException(ex)}");
                return (new List<PSObject>(), log.ToString());
            }

            if (ps.HadErrors)
            {
                foreach (var err in ps.Streams.Error)
                {
                    var msg = err.ToString();
                    if (!string.IsNullOrEmpty(err.FullyQualifiedErrorId))
                        msg += $" [{err.FullyQualifiedErrorId}]";
                    if (err.Exception != null)
                        msg += " | " + DescribeException(err.Exception);
                    log.AppendLine("ОШИБКА: " + msg);
                }
            }
            else
            {
                log.AppendLine($"OK: {cmdlet} -> {results.Count} объект(ов)");
                if (!string.IsNullOrWhiteSpace(ComputerName))
                    RecordSuccessfulRemote(ComputerName.Trim());
            }

            return (results, log.ToString());
        }

        /// <summary>
        /// Запоминает сервер, к которому только что успешно обратились, в начале истории
        /// (AppSettings, ключ RemoteServerHistory). История ограничена 10 последними уникальными
        /// именами - без разбора это могло бы расти бесконечно.
        /// </summary>
        private static void RecordSuccessfulRemote(string server)
        {
            var list = AppSettings.GetList("RemoteServerHistory");
            if (list.Count > 0 && string.Equals(list[0], server, StringComparison.OrdinalIgnoreCase))
                return; // уже наверху истории - ничего менять не нужно, лишний раз не пишем на диск

            list.RemoveAll(s => string.Equals(s, server, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, server);
            if (list.Count > 10) list = list.Take(10).ToList();
            AppSettings.SetList("RemoteServerHistory", list);
        }

        /// <summary>
        /// Форматирует список PSObject в читаемые строки для вывода в ListBox/TextBox.
        /// Если properties не заданы - выводит все свойства первого объекта.
        /// </summary>
        public static List<string> FormatObjects(List<PSObject> objects, params string[] properties)
        {
            var lines = new List<string>();
            if (objects == null || objects.Count == 0)
            {
                lines.Add("(пусто)");
                return lines;
            }

            foreach (var obj in objects)
            {
                var props = properties != null && properties.Length > 0
                    ? properties
                    : obj.Properties.Select(p => p.Name).ToArray();

                var parts = props.Select(p =>
                {
                    var prop = obj.Properties[p];
                    var val = prop?.Value?.ToString() ?? "";
                    return $"{p}={val}";
                });

                lines.Add(string.Join("  |  ", parts));
            }

            return lines;
        }

        public static List<string> GetStringProperty(List<PSObject> objects, string property)
        {
            return objects
                .Select(o => o.Properties[property]?.Value?.ToString())
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
        }

        /// <summary>
        /// Многие свойства DNS-объектов (ClientSubnet, ZoneScope и т.п.) на самом деле
        /// массивы/коллекции, а не простые строки - обычный ToString() на них выдаёт либо
        /// пустоту, либо "System.Object[]". Этот метод разворачивает такие значения в текст.
        /// </summary>
        public static string FlattenPropertyValue(object value)
        {
            if (value == null) return "";

            if (value is string s) return s;

            if (value is System.Collections.IEnumerable enumerable)
            {
                var items = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    // Элемент коллекции сам может быть CIM-объектом с полезными свойствами
                    // (например условие политики) - пробуем достать что-то читаемое из него.
                    var itemPsObj = PSObject.AsPSObject(item);
                    var itemText = item as string ?? item.ToString();
                    if (!(item is string) && !(item is ValueType) && itemPsObj.Properties.Any())
                    {
                        var readable = itemPsObj.Properties
                            .Select(p => p.Value?.ToString())
                            .FirstOrDefault(v => !string.IsNullOrEmpty(v) && v != itemText);
                        if (!string.IsNullOrEmpty(readable)) itemText = readable;
                    }
                    items.Add(itemText);
                }
                return string.Join(",", items);
            }

            return value.ToString();
        }

        /// <summary>
        /// RecordData у Get-DnsServerResourceRecord - это вложенный CIM-объект, чей ToString()
        /// показывает только имя типа (например "DnsServerResourceRecordA"), а не IP/значение.
        /// Этот метод достаёт настоящее значение по известным именам свойств для разных типов записей.
        /// </summary>
        public static string DescribeRecordData(object recordDataValue, string recordType)
        {
            if (recordDataValue == null) return "";

            var psObj = PSObject.AsPSObject(recordDataValue);
            var typeUpper = (recordType ?? "").ToUpperInvariant();

            // SRV - особый случай: одного target недостаточно, важны ещё порт/приоритет/вес.
            if (typeUpper == "SRV")
            {
                var target = psObj.Properties["DomainName"]?.Value?.ToString() ?? "";
                var port = psObj.Properties["Port"]?.Value?.ToString() ?? "";
                var priority = psObj.Properties["Priority"]?.Value?.ToString() ?? "";
                var weight = psObj.Properties["Weight"]?.Value?.ToString() ?? "";
                return $"{target}:{port} (priority={priority}, weight={weight})";
            }

            string[] candidateProps = typeUpper switch
            {
                "A" => new[] { "IPv4Address" },
                "AAAA" => new[] { "IPv6Address" },
                "CNAME" => new[] { "HostNameAlias" },
                "NS" => new[] { "NameServer" },
                "MX" => new[] { "MailExchange" },
                "TXT" => new[] { "DescriptiveText" },
                "PTR" => new[] { "PtrDomainName" },
                "SOA" => new[] { "PrimaryServer" },
                _ => Array.Empty<string>()
            };

            foreach (var propName in candidateProps)
            {
                var val = psObj.Properties[propName]?.Value;
                if (val != null) return val.ToString();
            }

            // Тип не из списка выше (или имя свойства другое в этой версии Windows) -
            // берём первое похожее по смыслу свойство, а не сырое имя класса.
            var fallback = psObj.Properties
                .FirstOrDefault(p => p.Name.Contains("Address") || p.Name.Contains("Server") ||
                                      p.Name.Contains("Name") || p.Name.Contains("Text") || p.Name.Contains("Domain"));
            return fallback?.Value?.ToString() ?? "";
        }

        /// <summary>
        /// Windows DNS-командлеты часто оборачивают реальную причину ошибки в CimException,
        /// у которого верхнее Message - это общая фраза ("Не удалось создать запись ресурса...").
        /// Настоящий код ошибки обычно лежит в доп. полях (StatusCode/NativeErrorCode/ErrorData),
        /// которые здесь достаются через рефлексию - так не нужна прямая ссылка на сборку CIM.
        /// </summary>
        private static string DescribeException(System.Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);

            foreach (var propName in new[] { "StatusCode", "NativeErrorCode", "ErrorSource", "MessageId" })
            {
                var prop = ex.GetType().GetProperty(propName);
                var val = prop?.GetValue(ex);
                if (val != null) sb.Append($" | {propName}={val}");
            }

            var errorDataProp = ex.GetType().GetProperty("ErrorData");
            var errorData = errorDataProp?.GetValue(ex);
            string errorCode = null;
            if (errorData != null)
            {
                try
                {
                    var psObj = PSObject.AsPSObject(errorData);
                    foreach (var p in psObj.Properties)
                    {
                        if (p.Value == null) continue;
                        if (p.Name == "error_Code") errorCode = p.Value.ToString();
                        if (p.Value is System.Collections.IEnumerable en && !(p.Value is string))
                        {
                            foreach (var item in en)
                                if (item != null) sb.Append($" | {p.Name}.item={item}");
                        }
                        else
                        {
                            sb.Append($" | {p.Name}={p.Value}");
                        }
                    }
                }
                catch { /* ErrorData не всегда доступен как PSObject - пропускаем без падения */ }
            }

            var hint = FriendlyHintForDnsErrorCode(errorCode);
            if (!string.IsNullOrEmpty(hint)) sb.Append($" | ПОДСКАЗКА: {hint}");

            // Дублирующиеся InnerException (то же самое Message) пропускаем - шума не добавляют
            var inner = ex.InnerException;
            while (inner != null)
            {
                if (!string.Equals(inner.Message, ex.Message, StringComparison.Ordinal))
                    sb.Append(" → ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
                inner = inner.InnerException;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Человеческая расшифровка известных кодов ошибок Windows DNS Server (error_Code
        /// из ErrorData у CimException). Список пополняется по мере того, как встречаются
        /// новые коды - сейчас в нём только те, что реально всплывали в этом проекте.
        /// </summary>
        private static string FriendlyHintForDnsErrorCode(string code) => code switch
        {
            "9611" => "DNS_ERROR_ZONE_TYPE_ERROR - сервер отказал из-за типа зоны. Самая частая причина: " +
                      "это Secondary (вторичная) зона - она реплицируется с мастер-сервера и доступна только " +
                      "на чтение. Редактировать записи нужно на мастер-сервере зоны (см. MasterServers у " +
                      "Get-DnsServerZone), изменения сюда придут сами через зонный трансфер.",
            _ => null
        };
    }
}
