using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace DnsToolWinForms.Services
{
    /// <summary>Одна строка итога прогона «добавить недостающие PTR».</summary>
    public sealed class PtrBackfillResultRow
    {
        public string RecordName;
        public string Ip;
        public string RequiredZone; // обратная зона, куда попал бы PTR ("10.168.192.in-addr.arpa")
        public string Status;       // "OK" / "Уже есть" / "Нет reverse-зоны" / "Ошибка: <текст>"
    }

    /// <summary>
    /// Прогон по A/AAAA-записям scope и создание недостающих PTR-записей в зонах
    /// обратного просмотра. Логика без WinForms, тестируется через фейковый раннер.
    /// </summary>
    public sealed class ReverseDnsService
    {
        private readonly IDnsCommandRunner _runner;

        public ReverseDnsService(IDnsCommandRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        /// <summary>ARPA-имя обратной зоны, куда сервер положил бы PTR для этого адреса.</summary>
        public static string GetReverseZoneName(string ipText)
        {
            if (!IPAddress.TryParse((ipText ?? "").Trim(), out var ip))
                return null;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // ip6.arpa: имя строится из полубайт адреса в СТРОГО обратном порядке -
                // от последнего полубайта адреса к первому (по байту с конца, в байте
                // сначала младший полубайт, потом старший).
                var bytes = ip.GetAddressBytes();
                var sb = new System.Text.StringBuilder();
                for (var i = bytes.Length - 1; i >= 0; i--)
                {
                    sb.Append((bytes[i] & 0x0F).ToString("x")).Append('.')
                      .Append((bytes[i] >> 4).ToString("x")).Append('.');
                }
                sb.Append("ip6.arpa");
                return sb.ToString();
            }
            return null;
        }

        /// <summary>
        /// Имя наиболее длинной СУЩЕСТВУЮЩЕЙ зоны, содержащей этот PTR
        /// (для 10.168.192.in-addr.arpa сначала пробуем /24-имя, потом /16, потом /8).
        /// Возвращает null, если подходящей обратной зоны нет ни одной.
        /// </summary>
        public static string FindExistingReverseZone(string ipText, ISet<string> knownZones)
        {
            var full = GetReverseZoneName(ipText);
            if (full == null) return null;

            foreach (var candidate in GetReverseZoneCandidates(full))
            {
                if (knownZones.Contains(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>
        /// Кандидаты зон от самой длинной (/24, /96 для IPv6) к короткой (/8, /16 для IPv6) -
        /// как вычисляет имя сам DNS Server при -CreatePtr: PTR кладётся в самую длинную
        /// существующую зону.
        /// </summary>
        public static IEnumerable<string> GetReverseZoneCandidates(string fullReverseName)
        {
            const string v4Suffix = ".in-addr.arpa";
            const string v6Suffix = ".ip6.arpa";

            if (fullReverseName.EndsWith(v4Suffix, StringComparison.OrdinalIgnoreCase))
            {
                var labels = fullReverseName.Substring(0, fullReverseName.Length - v4Suffix.Length).Split('.');
                for (var take = labels.Length; take >= 1; take--) // минимум /8 = 1 октет ("192.in-addr.arpa")
                    yield return string.Join(".", labels, labels.Length - take, take) + v4Suffix;
            }
            else if (fullReverseName.EndsWith(v6Suffix, StringComparison.OrdinalIgnoreCase))
            {
                var labels = fullReverseName.Substring(0, fullReverseName.Length - v6Suffix.Length).Split('.');
                for (var take = labels.Length; take >= 4; take -= 4) // nibble-границы: /16 = 4 полубайта
                    yield return string.Join(".", labels, labels.Length - take, take) + v6Suffix;
            }
        }

        /// <summary>
        /// Строит имя PTR-записи - часть адреса, которую нужно отдать как Name в обратной зоне.
        /// Для зоны "10.168.192.in-addr.arpa" и адреса "192.168.10.5" это "5";
        /// для /16-зоны "168.192.in-addr.arpa" - "10.5". IPv6 аналогично по nibble-границам.
        /// </summary>
        public static string BuildPtrName(string fullReverseName, string existingZoneName)
        {
            if (string.IsNullOrEmpty(fullReverseName) || string.IsNullOrEmpty(existingZoneName)) return null;
            if (!fullReverseName.EndsWith(existingZoneName, StringComparison.OrdinalIgnoreCase)) return null;
            var prefix = fullReverseName.Substring(0, fullReverseName.Length - existingZoneName.Length);
            // Имя записи относительно зоны: "5" + "." + "10.168.192.in-addr.arpa".
            // Точка перед зоной обязательна - иначе зона совпала как часть чужой метки.
            if (prefix.Length == 0 || !prefix.EndsWith(".")) return null;
            return prefix.Substring(0, prefix.Length - 1);
        }

        /// <summary>
        /// Полный прогон: A/AAAA-записи scope -> недостающие PTR. Пропускает wildcard ("*")
        /// и специальные адреса (link-local/loopback/multicast) - PTR для них не имеет смысла.
        /// Не создаёт PTR на адрес без существующей обратной зоны (статус "Нет reverse-зоны") -
        /// осознанное решение: автосоздание зон тут неожиданно. reportProgress - процент 0..100
        /// (вызывается из фонового потока, вызывающий обязан маршалить в UI сам),
        /// reportStatus - текст статуса, isCancelled - кооперативная отмена,
        /// onCreated - вызывается после КАЖДОГО успешного создания PTR (для журнала изменений).
        /// </summary>
        public async Task<List<PtrBackfillResultRow>> BackfillPtrsAsync(
            string zoneName, string scopeName,
            Action<int> reportProgress,
            Action<string> reportStatus,
            Func<bool> isCancelled = null,
            Action<string, string, string> onCreated = null)
        {
            var rows = new List<PtrBackfillResultRow>();

            // 1. Записи выбранного scope.
            var recParams = new Dictionary<string, object> { ["ZoneName"] = zoneName, ["ZoneScope"] = scopeName };
            var (records, recLog) = await Task.Run(() => _runner.Invoke("Get-DnsServerResourceRecord", recParams));
            if (!DnsOutcome.WasSuccess(recLog))
                throw new InvalidOperationException("Не удалось получить записи scope: " + recLog);

            var addressRecords = new List<(string Name, string Type, string Ip)>();
            foreach (var r in records)
            {
                var type = r.Properties["RecordType"]?.Value?.ToString()?.ToUpperInvariant();
                if (type != "A" && type != "AAAA") continue;
                var name = r.Properties["HostName"]?.Value?.ToString() ?? "";
                if (name.Contains("*")) continue; // wildcard -> PTR не имеет смысла
                var ip = DnsHelper.DescribeRecordData(r.Properties["RecordData"]?.Value, type);
                if (string.IsNullOrEmpty(ip)) continue;
                if (IsSpecialPurposeAddress(ip)) continue;
                addressRecords.Add((name, type, ip));
            }

            // 2. Все зоны сервера -> обратные, с разбором их "сети" для длиннейшего совпадения.
            var (zones, zonesLog) = await Task.Run(() => _runner.Invoke("Get-DnsServerZone"));
            if (!DnsOutcome.WasSuccess(zonesLog))
                throw new InvalidOperationException("Не удалось получить список зон сервера: " + zonesLog);

            var reverseZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in zones)
            {
                var zName = z.Properties["ZoneName"]?.Value?.ToString();
                if (string.IsNullOrEmpty(zName)) continue;
                var zType = z.Properties["ZoneType"]?.Value?.ToString() ?? "";
                if (zType.Equals("Forwarder", StringComparison.OrdinalIgnoreCase) ||
                    zType.Equals("Stub", StringComparison.OrdinalIgnoreCase)) continue;
                var isReverse = DnsHelper.GetBool(z, "IsReverseLookupZone")
                                || zName.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase)
                                || zName.EndsWith(".ip6.arpa", StringComparison.OrdinalIgnoreCase);
                if (isReverse) reverseZones.Add(zName.TrimEnd('.'));
            }

            // 3. Существующие PTR по ВСЕМ обратным зонам (они лежат в reverse-зонах, не в прямой):
            //    один запрос на зону - обычно их единицы. Ключ "зона|имя" ("10.168.192.in-addr.arpa|5").
            var existingPtrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rz in reverseZones)
            {
                var ptrParams = new Dictionary<string, object> { ["ZoneName"] = rz, ["RRType"] = "PTR" };
                var (ptrRecords, ptrLog) = await Task.Run(() => _runner.Invoke("Get-DnsServerResourceRecord", ptrParams));
                if (!DnsOutcome.WasSuccess(ptrLog)) continue;
                foreach (var pr in ptrRecords)
                {
                    var host = pr.Properties["HostName"]?.Value?.ToString();
                    if (!string.IsNullOrEmpty(host)) existingPtrs.Add($"{rz}|{host}");
                }
            }

            // 4. Прогон: есть ли PTR; создание недостающего + прогресс.
            var total = addressRecords.Count;
            reportStatus?.Invoke($"Записей A/AAAA к проверке: {total}");
            var done = 0;
            foreach (var (name, type, ip) in addressRecords)
            {
                if (isCancelled?.Invoke() == true) break;

                var full = GetReverseZoneName(ip);
                var targetZone = full != null ? FindExistingReverseZone(ip, reverseZones) : null;

                if (targetZone == null)
                {
                    rows.Add(new PtrBackfillResultRow { RecordName = name, Ip = ip, RequiredZone = full ?? "?", Status = "Нет reverse-зоны" });
                }
                else
                {
                    var ptrName = BuildPtrName(full, targetZone);
                    var key = $"{targetZone}|{ptrName}";
                    if (existingPtrs.Contains(key))
                    {
                        rows.Add(new PtrBackfillResultRow { RecordName = name, Ip = ip, RequiredZone = targetZone, Status = "Уже есть" });
                    }
                    else
                    {
                        try
                        {
                            var ptrDomain = NormalizePtrTarget(name, zoneName);
                            var addParams = new Dictionary<string, object>
                            {
                                ["ZoneName"] = targetZone,
                                ["Name"] = ptrName,
                                ["PtrDomainName"] = ptrDomain
                            };
                            var (_, addLog) = await Task.Run(() => _runner.Invoke("Add-DnsServerResourceRecordPtr", addParams));
                            if (DnsOutcome.WasSuccess(addLog))
                            {
                                existingPtrs.Add(key); // дубль внутри этого же прогона не пойдёт на сервер повторно
                                rows.Add(new PtrBackfillResultRow { RecordName = name, Ip = ip, RequiredZone = targetZone, Status = "OK" });
                                onCreated?.Invoke(targetZone, ptrName, ptrDomain);
                            }
                            else
                            {
                                var shortErr = DnsErrorMessages.BuildShortError(addLog, addParams) ?? addLog;
                                rows.Add(new PtrBackfillResultRow { RecordName = name, Ip = ip, RequiredZone = targetZone, Status = "Ошибка: " + shortErr });
                            }
                        }
                        catch (Exception ex)
                        {
                            rows.Add(new PtrBackfillResultRow { RecordName = name, Ip = ip, RequiredZone = targetZone, Status = "Ошибка: " + ex.Message });
                        }
                    }
                }

                done++;
                var pct = total == 0 ? 100 : done * 100 / total;
                reportProgress?.Invoke(pct);
                reportStatus?.Invoke($"Прогон: {done} из {total}...");
            }

            return rows;
        }

        /// <summary>
        /// FQDN цели PTR: относительное имя хоста получает суффикс зоны ("srv2" -> "srv2.corp.local"),
        /// полное имя или алиас со своей точкой остаётся как есть.
        /// </summary>
        private static string NormalizePtrTarget(string hostName, string zoneName)
        {
            var n = (hostName ?? "").Trim();
            if (string.IsNullOrEmpty(n) || n == "@" || n.EndsWith("."))
                return n;

            var suffix = "." + (zoneName ?? "").Trim();
            if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return n;
            return n + suffix;
        }

        /// <summary>Link-local, loopback, multicast, broadcast - PTR для таких адресов не создают.</summary>
        public static bool IsSpecialPurposeAddress(string ipText)
        {
            if (!IPAddress.TryParse((ipText ?? "").Trim(), out var ip)) return true;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 127 || b[0] == 0 || b[0] >= 224) return true;    // loopback/this-network/multicast+broadcast
                if (b[0] == 169 && b[1] == 254) return true;                 // link-local APIPA
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;   // CGNAT 100.64/10
                if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;      // TEST-NET-1
                if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;  // benchmark
                if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;   // TEST-NET-2
                if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;    // TEST-NET-3
                return false;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 0xff) return true;                                    // multicast
                if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;           // link-local fe80::/10
                var allZero = b.All(x => x == 0);
                if (allZero) return true;                                         // ::
                var loopback = b.Take(15).All(x => x == 0) && b[15] == 1;         // ::1
                return loopback;
            }

            return true;
        }
    }
}
