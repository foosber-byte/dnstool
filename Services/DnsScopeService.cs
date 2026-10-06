using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Threading.Tasks;

namespace DnsToolWinForms.Services
{
    /// <summary>
    /// Управление зонами и Zone Scopes (Get/Add/Remove-DnsServerZone, Add/Remove-
    /// DnsServerZoneScope) поверх IDnsCommandRunner - без WinForms-контролов,
    /// тестируется через фейковый раннер. Выборки только имён - в DnsZoneService.
    /// </summary>
    public sealed class DnsScopeService
    {
        private readonly IDnsCommandRunner _runner;

        public DnsScopeService(IDnsCommandRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        /// <summary>Все зоны текущего сервера (сырые объекты - дерево вкладки классифицирует их само).</summary>
        public async Task<(List<PSObject> Results, string Log)> GetAllZonesAsync()
        {
            return await Task.Run(() => _runner.Invoke("Get-DnsServerZone"));
        }

        /// <summary>Конкретная зона по имени (источник: AD/файл для Primary, мастер-серверы для Secondary/Stub).</summary>
        public async Task<(List<PSObject> Results, string Log)> GetZoneAsync(string zoneName)
        {
            var parameters = new Dictionary<string, object> { ["Name"] = zoneName };
            return await Task.Run(() => _runner.Invoke("Get-DnsServerZone", parameters));
        }

        /// <summary>
        /// Создаёт первичную зону. ReplicationScope ("Domain"/"Forest") - для AD-зоны;
        /// для файловой зоны задаётся ZoneFile, а ReplicationScope не передаётся вовсе
        /// (AD о такой зоне ничего не знает, всё хранится в .dns-файле на диске).
        /// </summary>
        public async Task<(bool Success, string Log)> AddPrimaryZoneAsync(string zoneName, string replicationScope, string zoneFile)
        {
            var parameters = new Dictionary<string, object> { ["Name"] = zoneName };
            if (!string.IsNullOrEmpty(replicationScope)) parameters["ReplicationScope"] = replicationScope;
            if (!string.IsNullOrEmpty(zoneFile)) parameters["ZoneFile"] = zoneFile;
            var (_, log) = await Task.Run(() => _runner.Invoke("Add-DnsServerPrimaryZone", parameters));
            return (DnsOutcome.WasSuccess(log), log);
        }

        public async Task<(bool Success, string Log)> RemoveZoneAsync(string zoneName)
        {
            var parameters = new Dictionary<string, object> { ["Name"] = zoneName, ["Force"] = true };
            var (_, log) = await Task.Run(() => _runner.Invoke("Remove-DnsServerZone", parameters));
            return (DnsOutcome.WasSuccess(log), log);
        }

        public async Task<(bool Success, string Log)> AddScopeAsync(string zoneName, string scopeName)
        {
            var parameters = new Dictionary<string, object> { ["ZoneName"] = zoneName, ["Name"] = scopeName };
            var (_, log) = await Task.Run(() => _runner.Invoke("Add-DnsServerZoneScope", parameters));
            return (DnsOutcome.WasSuccess(log), log);
        }

        public async Task<(bool Success, string Log)> RemoveScopeAsync(string zoneName, string scopeName)
        {
            var parameters = new Dictionary<string, object> { ["ZoneName"] = zoneName, ["Name"] = scopeName, ["Force"] = true };
            var (_, log) = await Task.Run(() => _runner.Invoke("Remove-DnsServerZoneScope", parameters));
            return (DnsOutcome.WasSuccess(log), log);
        }
    }
}
