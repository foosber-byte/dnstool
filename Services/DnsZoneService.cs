using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Threading.Tasks;

namespace DnsToolWinForms.Services
{
    /// <summary>
    /// Работа с зонами DNS поверх IDnsCommandRunner: классификация зон (применимость
    /// Zone Scopes) и выборки имён зон / scope'ов / клиентских подсетей. Без
    /// WinForms-контролов, тестируется через фейковый раннер.
    /// </summary>
    public sealed class DnsZoneService
    {
        private readonly IDnsCommandRunner _runner;

        public DnsZoneService(IDnsCommandRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        /// <summary>
        /// Зона, к которой применимы Zone Scopes (в дереве это "прямые"/"обратные", но не
        /// "прочие"): не служебная авто-зона (TrustAnchors, корневые подсказки и т.п.) и
        /// не условная пересылка / stub.
        /// </summary>
        public static bool IsScopeCapableZone(PSObject z)
        {
            if (z == null) return false;
            var zoneName = z.Properties["ZoneName"]?.Value?.ToString();
            if (IsServiceAutoZone(z, zoneName)) return false;
            var t = z.Properties["ZoneType"]?.Value?.ToString() ?? "";
            return !t.Equals("Forwarder", StringComparison.OrdinalIgnoreCase)
                && !t.Equals("Stub", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Служебная зона, которую стандартная оснастка dnsmgmt.msc в обычном (не "расширенном")
        /// виде не показывает: помеченные IsAutoCreated, а также TrustAnchors (DNSSEC) и корневые
        /// подсказки ".", у которых этот флаг на контроллере домена бывает не выставлен - поэтому
        /// дополнительно ловим их по имени. Редактировать/смотреть scope в них всё равно нельзя
        /// (WIN32 9611/9603), а сырой дамп ошибки в выводе только путает.
        /// </summary>
        public static bool IsServiceAutoZone(PSObject z, string zoneName)
        {
            if (z != null && DnsHelper.GetBool(z, "IsAutoCreated")) return true;

            var zoneType = z?.Properties["ZoneType"]?.Value?.ToString() ?? "";
            if (zoneType.Equals("Cache", StringComparison.OrdinalIgnoreCase)) return true; // псевдо-зона кэша / корневых подсказок

            var n = (zoneName ?? "").Trim().TrimEnd('.');
            return n.Length == 0 // корневые подсказки "."
                || n.Equals("TrustAnchors", StringComparison.OrdinalIgnoreCase)
                || n.Equals("0.in-addr.arpa", StringComparison.OrdinalIgnoreCase)
                || n.Equals("127.in-addr.arpa", StringComparison.OrdinalIgnoreCase)
                || n.Equals("255.in-addr.arpa", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Имена зон (только те, что поддерживают Zone Scopes) текущего сервера, в порядке выдачи сервера.</summary>
        public async Task<(List<string> Names, string Log)> GetScopeCapableZoneNamesAsync()
        {
            var (results, log) = await Task.Run(() => _runner.Invoke("Get-DnsServerZone"));
            var names = results
                .Where(IsScopeCapableZone)
                .Select(o => o.Properties["ZoneName"]?.Value?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            return (names, log);
        }

        /// <summary>Имена клиентских подсетей текущего сервера, отсортированные по имени.</summary>
        public async Task<(List<string> Names, string Log)> GetClientSubnetNamesAsync()
        {
            var (results, log) = await Task.Run(() => _runner.Invoke("Get-DnsServerClientSubnet"));
            var names = results
                .Select(r => r.Properties["Name"]?.Value?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return (names, log);
        }

        /// <summary>
        /// Имена scope'ов зоны. Get-DnsServerZoneScope в зависимости от версии возвращает их
        /// то в свойстве ZoneScope, то в Name - пробуем оба.
        /// </summary>
        public async Task<(List<string> Names, string Log)> GetZoneScopeNamesAsync(string zoneName)
        {
            var parameters = new Dictionary<string, object> { ["ZoneName"] = zoneName };
            var (results, log) = await Task.Run(() => _runner.Invoke("Get-DnsServerZoneScope", parameters));
            var names = DnsHelper.GetStringProperty(results, "ZoneScope");
            if (names.Count == 0) names = DnsHelper.GetStringProperty(results, "Name");
            return (names, log);
        }
    }
}
