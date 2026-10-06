using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Text;

namespace DnsToolWinForms.Services
{
    /// <summary>
    /// Узел дерева записей - группировка по составным именам (admin.pro32connect -> папка
    /// "pro32connect" содержит запись "admin"), как это делает стандартная оснастка dnsmgmt.msc.
    /// Строится из уже загруженного плоского списка записей scope - без обращения к серверу.
    /// </summary>
    public class RecordTreeNode
    {
        public string Label;
        public RecordTreeNode Parent;
        public Dictionary<string, RecordTreeNode> Children = new Dictionary<string, RecordTreeNode>(StringComparer.OrdinalIgnoreCase);
        public List<PSObject> RecordsHere = new List<PSObject>(); // записи, чьё полное имя заканчивается ИМЕННО на этом узле
    }

    /// <summary>
    /// Чистая (без UI) логика записей DNS: построение команд добавления, нормализация имён,
    /// группировка в дерево папок и разбор .dns-файлов scope (файловый обходной путь для
    /// Secondary/read-only зон). Перенесено из MainForm без изменения поведения.
    /// </summary>
    public static class DnsRecordService
    {
        /// <summary>
        /// DNS-командлеты ждут ИМЯ ОТНОСИТЕЛЬНО ЗОНЫ (например "bla.bla" для записи внутри
        /// "bla.bla.corp.local" в зоне "corp.local") и сами дописывают зону при создании.
        /// Если пользователь ввёл имя целиком, с зоной на конце ("bla.bla.corp.local") -
        /// зона приклеится ВТОРОЙ раз ("bla.bla.corp.local.corp.local"). Срезаем суффикс
        /// зоны, если он есть, чтобы многоуровневые имена (bla.bla, а не просто bla)
        /// создавались как в обычной оснастке - вложенной записью, а не дублем зоны.
        ///
        /// Хвостовая точка ("ssheiee0j1.a.trbcdn.net.") - стандартный DNS-маркер "это абсолютное
        /// FQDN, не относительное имя внутри текущей зоны" (нужно, например, чтобы вписать в
        /// scope запись на чужой, посторонний домен - приём для подмены/sinkhole внешнего имени).
        /// Такое имя не имеет отношения к суффиксу текущей зоны - трогать его нормализацией
        /// суффикса нельзя, иначе абсолютное имя случайно смешается с логикой относительных имён.
        /// </summary>
        public static string NormalizeRecordName(string name, string zoneName)
        {
            if (string.IsNullOrEmpty(name) || name == "@" || string.IsNullOrEmpty(zoneName))
                return name;

            if (name.EndsWith(".", StringComparison.Ordinal))
                return name; // абсолютный FQDN - оставляем как есть, без нормализации суффикса

            var suffix = "." + zoneName;
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - suffix.Length);

            // Точное совпадение с именем зоны целиком - это и есть корень зоны
            if (string.Equals(name, zoneName, StringComparison.OrdinalIgnoreCase))
                return "@";

            return name;
        }

        /// <summary>
        /// Восстанавливает DNS-суффикс текущей папки в дереве - обход СНИЗУ ВВЕРХ (от узла к
        /// корню scope) уже даёт метки в правильном порядке для конкатенации (без разворота):
        /// если сейчас внутри "_msdcs > dc > _tcp", суффикс будет "_tcp.dc._msdcs". Корень scope
        /// (Label == "" и Parent == null) не включается.
        /// </summary>
        public static string GetFolderPathSuffix(RecordTreeNode node)
        {
            var parts = new List<string>();
            var current = node;
            while (current != null && current.Parent != null)
            {
                parts.Add(current.Label);
                current = current.Parent;
            }
            return string.Join(".", parts);
        }

        /// <summary>
        /// Собирает командлет и параметры для добавления записи любого из 6 типов.
        /// Общий код для обычного добавления (AddRecordToScopeAsync) и для редактирования
        /// (EditSelectedRecordAsync - там запись пересоздаётся с новыми значениями).
        /// Каждый тип записи - отдельный выделенный командлет (Add-DnsServerResourceRecord<Тип>),
        /// а не универсальный Add-DnsServerResourceRecord -A/-AAAA/... - именно выделенные командлеты
        /// надёжно работают со scope (см. историю переписки: универсальный вариант падал на файловых зонах).
        /// </summary>
        public static (string Cmdlet, Dictionary<string, object> Parameters) BuildAddRecordCommand(
            string zoneName, string scopeName, string type, string name, string value,
            string priorityText, string weightText, string portText)
        {
            var parameters = new Dictionary<string, object>
            {
                ["ZoneName"] = zoneName,
                ["ZoneScope"] = scopeName,
                ["Name"] = name
            };

            string cmdlet;
            switch (type)
            {
                case "AAAA":
                    cmdlet = "Add-DnsServerResourceRecordAAAA";
                    parameters["IPv6Address"] = value;
                    break;
                case "CNAME":
                    cmdlet = "Add-DnsServerResourceRecordCName";
                    parameters["HostNameAlias"] = value;
                    break;
                case "PTR":
                    cmdlet = "Add-DnsServerResourceRecordPtr";
                    parameters["PtrDomainName"] = value;
                    break;
                case "NS":
                    // Add-DnsServerResourceRecordNS не существует (проверено отдельно по документации
                    // Microsoft - в отличие от MX ниже, у NS выделенного командлета нет).
                    cmdlet = "Add-DnsServerResourceRecord";
                    parameters["NS"] = true;
                    parameters["NameServer"] = value;
                    break;
                case "MX":
                    // В отличие от NS/TXT/SRV, у MX ЕСТЬ выделенный командлет - проверено отдельно
                    // по документации Microsoft (легко было по инерции отправить его в общий с NS/TXT/SRV).
                    cmdlet = "Add-DnsServerResourceRecordMX";
                    parameters["MailExchange"] = value;
                    parameters["Preference"] = (ushort)ParseIntOrDefault(priorityText, 10);
                    break;
                case "TXT":
                    // Add-DnsServerResourceRecordTxt НЕ СУЩЕСТВУЕТ как отдельный командлет
                    // (проверено по официальной документации Microsoft) - только универсальный
                    // Add-DnsServerResourceRecord с ключом -Txt.
                    cmdlet = "Add-DnsServerResourceRecord";
                    parameters["Txt"] = true;
                    parameters["DescriptiveText"] = value;
                    break;
                case "SRV":
                    // Аналогично TXT - Add-DnsServerResourceRecordSrv тоже не существует.
                    cmdlet = "Add-DnsServerResourceRecord";
                    parameters["Srv"] = true;
                    parameters["DomainName"] = value;
                    parameters["Priority"] = (ushort)ParseIntOrDefault(priorityText, 10);
                    parameters["Weight"] = (ushort)ParseIntOrDefault(weightText, 10);
                    parameters["Port"] = (ushort)ParseIntOrDefault(portText, 443);
                    break;
                default: // "A"
                    cmdlet = "Add-DnsServerResourceRecordA";
                    parameters["IPv4Address"] = value;
                    break;
            }

            return (cmdlet, parameters);
        }

        public static int ParseIntOrDefault(string s, int fallback) => int.TryParse(s, out var v) ? v : fallback;

        public static string EnsureTrailingDot(string fqdn) =>
            string.IsNullOrEmpty(fqdn) || fqdn.EndsWith(".") ? fqdn : fqdn + ".";

        /// <summary>
        /// dnscmd.exe входит в саму роль DNS Server (не отдельный пакет) - должен быть на любом
        /// сервере, где эта роль установлена. /ZoneReload перечитывает конкретную зону с диска,
        /// не трогая остальные зоны и не требуя перезапуска всей службы DNS.
        /// </summary>
        public static string RunDnscmdZoneReload(string zoneName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dnscmd.exe",
                    Arguments = $"/ZoneReload {zoneName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // dnscmd.exe на русской Windows тоже пишет в CP866 - без этого кракозябры.
                    StandardOutputEncoding = Encoding.GetEncoding(866),
                    StandardErrorEncoding = Encoding.GetEncoding(866)
                };

                using var proc = Process.Start(psi);
                var output = proc.StandardOutput.ReadToEnd().Trim();
                var error = proc.StandardError.ReadToEnd().Trim();
                proc.WaitForExit(15000);

                if (!string.IsNullOrEmpty(error)) return $"ОШИБКА dnscmd: {error}";
                return $"OK: {(string.IsNullOrEmpty(output) ? "зона перезагружена" : output)}";
            }
            catch (Exception ex)
            {
                return "ОШИБКА: не удалось запустить dnscmd.exe - " + ex.Message +
                       ". Убедись, что команда доступна (входит в роль DNS Server), либо перезагрузи зону вручную через оснастку.";
            }
        }

        /// <summary>
        /// Ищет в строках .dns-файла scope все строки, соответствующие записи rec
        /// (имя + тип + значение). Возвращает индексы строк; null - если тип записи
        /// файловым удалением не поддерживается.
        /// </summary>
        public static List<int> FindScopeFileRecordLines(List<string> lines, string zoneName, PSObject rec)
        {
            var wantOwner = NormalizeZoneOwner(rec.Properties["HostName"]?.Value?.ToString(), zoneName);
            var wantType = (rec.Properties["RecordType"]?.Value?.ToString() ?? "").ToUpperInvariant();
            var wantData = ZoneRdataFromRecord(rec, wantType);
            if (wantData == null) return null; // тип не поддерживается

            var result = new List<int>();
            string lastOwner = null;
            for (int i = 0; i < lines.Count; i++)
            {
                var raw = StripZoneFileComment(lines[i]);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (raw.TrimStart().StartsWith("$")) continue; // $ORIGIN / $TTL / $GENERATE

                var ownerInline = !char.IsWhiteSpace(raw[0]);
                var tok = TokenizeZoneFileLine(raw);
                if (tok.Count == 0) continue;

                int idx = 0;
                string owner;
                if (ownerInline) { owner = tok[0]; idx = 1; lastOwner = owner; }
                else owner = lastOwner;
                if (owner == null) continue;

                // необязательные TTL и CLASS в любом порядке перед типом
                for (int guard = 0; guard < 2 && idx < tok.Count; guard++)
                {
                    if (IsZoneFileTtl(tok[idx]) || IsZoneFileClass(tok[idx])) { idx++; continue; }
                    break;
                }
                if (idx >= tok.Count) continue;

                var type = tok[idx].ToUpperInvariant();
                idx++;
                if (type != wantType) continue;
                if (NormalizeZoneOwner(owner, zoneName) != wantOwner) continue;

                var rdata = string.Join(" ", tok.Skip(idx));
                if (ZoneRdataFromFile(rdata, type) == wantData) result.Add(i);
            }
            return result;
        }

        public static string StripZoneFileComment(string line)
        {
            var inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"') inQuotes = !inQuotes;
                else if (c == ';' && !inQuotes) return line.Substring(0, i);
            }
            return line;
        }

        public static List<string> TokenizeZoneFileLine(string line)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            var inQuotes = false;
            foreach (var c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; sb.Append(c); continue; }
                if (!inQuotes && (c == ' ' || c == '\t' || c == '(' || c == ')'))
                {
                    if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        public static bool IsZoneFileClass(string t) =>
            t.Equals("IN", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("CH", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("HS", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("CS", StringComparison.OrdinalIgnoreCase);

        public static bool IsZoneFileTtl(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            int i = 0;
            while (i < t.Length && char.IsDigit(t[i])) i++;
            if (i == 0) return false;
            if (i == t.Length) return true;                       // чистое число секунд
            return i == t.Length - 1 && "smhdwSMHDW".IndexOf(t[t.Length - 1]) >= 0; // 1h / 30m / 2w
        }

        /// <summary>Имя владельца записи -> сравнимая форма: "@" для вершины зоны, иначе относительное имя в нижнем регистре без хвостовой точки.</summary>
        public static string NormalizeZoneOwner(string name, string zone)
        {
            if (name == null) return null;
            name = name.Trim().TrimEnd('.');
            var z = (zone ?? "").Trim().TrimEnd('.');
            if (name.Length == 0 || name == "@") return "@";
            if (z.Length > 0 && name.Equals(z, StringComparison.OrdinalIgnoreCase)) return "@";
            if (z.Length > 0 && name.EndsWith("." + z, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - z.Length - 1);
            return name.ToLowerInvariant();
        }

        public static string NormalizeZoneName(string s) => (s ?? "").Trim().TrimEnd('.').ToLowerInvariant();

        public static string NormalizeZoneIp(string s)
        {
            s = (s ?? "").Trim();
            return System.Net.IPAddress.TryParse(s, out var ip) ? ip.ToString() : s.ToLowerInvariant();
        }

        /// <summary>Значение записи из объекта Get-DnsServerResourceRecord в нормализованную строку. null - тип не поддерживается файловым удалением.</summary>
        public static string ZoneRdataFromRecord(PSObject rec, string type)
        {
            var rd = PSObject.AsPSObject(rec.Properties["RecordData"]?.Value);
            if (rd == null) return null;
            string P(string n) => rd.Properties[n]?.Value?.ToString();
            switch (type)
            {
                case "A": return "A " + NormalizeZoneIp(P("IPv4Address"));
                case "AAAA": return "AAAA " + NormalizeZoneIp(P("IPv6Address"));
                case "CNAME": return "CNAME " + NormalizeZoneName(P("HostNameAlias"));
                case "NS": return "NS " + NormalizeZoneName(P("NameServer"));
                case "PTR": return "PTR " + NormalizeZoneName(P("PtrDomainName"));
                case "MX": return $"MX {ParseIntOrDefault(P("Preference"), 0)} {NormalizeZoneName(P("MailExchange"))}";
                case "SRV": return $"SRV {ParseIntOrDefault(P("Priority"), 0)} {ParseIntOrDefault(P("Weight"), 0)} {ParseIntOrDefault(P("Port"), 0)} {NormalizeZoneName(P("DomainName"))}";
                case "TXT": return "TXT " + NormalizeZoneTxt(rd.Properties["DescriptiveText"]?.Value);
                default: return null;
            }
        }

        /// <summary>То же значение, но разобранное из rdata-части строки .dns-файла.</summary>
        public static string ZoneRdataFromFile(string rdata, string type)
        {
            var p = (rdata ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            switch (type)
            {
                case "A": return p.Length >= 1 ? "A " + NormalizeZoneIp(p[0]) : "A ";
                case "AAAA": return p.Length >= 1 ? "AAAA " + NormalizeZoneIp(p[0]) : "AAAA ";
                case "CNAME": return p.Length >= 1 ? "CNAME " + NormalizeZoneName(p[0]) : "CNAME ";
                case "NS": return p.Length >= 1 ? "NS " + NormalizeZoneName(p[0]) : "NS ";
                case "PTR": return p.Length >= 1 ? "PTR " + NormalizeZoneName(p[0]) : "PTR ";
                case "MX": return p.Length >= 2 ? $"MX {ParseIntOrDefault(p[0], 0)} {NormalizeZoneName(p[1])}" : null;
                case "SRV": return p.Length >= 4 ? $"SRV {ParseIntOrDefault(p[0], 0)} {ParseIntOrDefault(p[1], 0)} {ParseIntOrDefault(p[2], 0)} {NormalizeZoneName(p[3])}" : null;
                case "TXT": return "TXT " + NormalizeZoneTxt(rdata);
                default: return null;
            }
        }

        /// <summary>TXT: и объект записи, и строка файла приводятся к склейке содержимого всех кавычечных сегментов без самих кавычек.</summary>
        public static string NormalizeZoneTxt(object value)
        {
            if (value == null) return "";
            IEnumerable<string> parts;
            if (value is string s)
            {
                var segs = new List<string>();
                var sb = new StringBuilder();
                var inQ = false;
                foreach (var c in s)
                {
                    if (c == '"') { if (inQ) { segs.Add(sb.ToString()); sb.Clear(); } inQ = !inQ; }
                    else if (inQ) sb.Append(c);
                }
                if (segs.Count == 0) segs.Add(s.Trim()); // строка без кавычек - как есть
                parts = segs;
            }
            else if (value is System.Collections.IEnumerable en)
            {
                var segs = new List<string>();
                foreach (var o in en) if (o != null) segs.Add(o.ToString());
                parts = segs;
            }
            else parts = new[] { value.ToString() };
            return string.Concat(parts);
        }

        /// <summary>
        /// Группирует плоский список записей в дерево по составным именам - так же, как это
        /// делает dnsmgmt.msc: "admin.pro32connect" -> папка "pro32connect" содержит запись "admin".
        /// Имя разбивается по точкам и ЧИТАЕТСЯ СПРАВА НАЛЕВО (правый сегмент - самый внешний
        /// уровень, ближе к корню зоны) - "_ldap._tcp.dc._msdcs" даёт путь _msdcs > dc > _tcp > _ldap.
        /// Работает на уже загруженных данных, без обращения к серверу.
        /// </summary>
        public static RecordTreeNode BuildRecordTree(List<PSObject> records)
        {
            var root = new RecordTreeNode { Label = "" };
            foreach (var rec in records)
            {
                var name = rec.Properties["HostName"]?.Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(name) || name == "@")
                {
                    root.RecordsHere.Add(rec);
                    continue;
                }

                var segments = name.Split('.');
                Array.Reverse(segments);

                var node = root;
                foreach (var seg in segments)
                {
                    if (!node.Children.TryGetValue(seg, out var child))
                    {
                        child = new RecordTreeNode { Label = seg, Parent = node };
                        node.Children[seg] = child;
                    }
                    node = child;
                }
                node.RecordsHere.Add(rec);
            }
            return root;
        }

        /// <summary>Считает записи во всём поддереве узла (для метки "N записей" у папки в списке).</summary>
        public static int CountRecordsRecursive(RecordTreeNode node)
        {
            var count = node.RecordsHere.Count;
            foreach (var child in node.Children.Values)
                count += CountRecordsRecursive(child);
            return count;
        }
    }
}
