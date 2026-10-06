using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Threading.Tasks;

namespace DnsToolWinForms.Services
{
    /// <summary>Клиентская подсеть DNS-сервера: имя + CIDR (IPv4, при отсутствии - IPv6).</summary>
    public sealed class DnsSubnet
    {
        public string Name { get; }
        public string Cidr { get; }

        public DnsSubnet(string name, string cidr)
        {
            Name = name ?? "";
            Cidr = cidr ?? "";
        }

        /// <summary>Строка вида "имя + выровненный CIDR" - так подсеть показывалась в списке вкладки.</summary>
        public string Display => string.IsNullOrEmpty(Cidr) ? Name : $"{Name,-25} {Cidr}";
    }

    /// <summary>
    /// Операции с клиентскими подсетями (Get/Add/Remove-DnsServerClientSubnet) поверх
    /// IDnsCommandRunner - без WinForms-контролов, тестируется через фейковый раннер.
    /// </summary>
    public sealed class DnsSubnetService
    {
        private readonly IDnsCommandRunner _runner;

        public DnsSubnetService(IDnsCommandRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        /// <summary>Проверка полей перед созданием подсети; null = всё заполнено.</summary>
        public static string ValidateAdd(string name, string cidr)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cidr))
                return "Заполни имя подсети и CIDR (например 10.0.1.0/24).";
            return null;
        }

        public async Task<(List<DnsSubnet> Items, string Log)> GetAllAsync()
        {
            var (results, log) = await Task.Run(() => _runner.Invoke("Get-DnsServerClientSubnet"));
            var items = new List<DnsSubnet>();
            foreach (var s in results)
            {
                var name = s.Properties["Name"]?.Value?.ToString() ?? "";
                var cidr = DnsHelper.FlattenPropertyValue(s.Properties["IPv4Subnet"]?.Value);
                if (string.IsNullOrEmpty(cidr)) cidr = DnsHelper.FlattenPropertyValue(s.Properties["IPv6Subnet"]?.Value);
                items.Add(new DnsSubnet(name, cidr));
            }
            return (items, log);
        }

        public async Task<(bool Success, string Log)> AddAsync(string name, string cidr)
        {
            var parameters = new Dictionary<string, object> { ["Name"] = name, ["IPv4Subnet"] = cidr };
            var (_, log) = await Task.Run(() => _runner.Invoke("Add-DnsServerClientSubnet", parameters));
            return (DnsOutcome.WasSuccess(log), log);
        }

        public async Task<(bool Success, string Log)> RemoveAsync(string name)
        {
            var parameters = new Dictionary<string, object> { ["Name"] = name, ["Force"] = true };
            var (_, log) = await Task.Run(() => _runner.Invoke("Remove-DnsServerClientSubnet", parameters));
            return (DnsOutcome.WasSuccess(log), log);
        }
    }
}
