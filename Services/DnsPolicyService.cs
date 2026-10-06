using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DnsToolWinForms.Services
{
    /// <summary>
    /// Политика разрешения запросов (Query Resolution Policy) как её показывает вкладка:
    /// имя, критерий-подсети (сырой и читаемый вид) и scope. SubnetRaw - то, что вернул
    /// сервер ("EQ,net_100,Old_DNS_redirect13"), SubnetDisplay - чистые имена через ", ".
    /// </summary>
    public sealed class DnsPolicy
    {
        public string Name { get; }
        public string SubnetRaw { get; }
        public string SubnetDisplay { get; }
        public string Scope { get; }

        public DnsPolicy(string name, string subnetRaw, string scope)
        {
            Name = name ?? "";
            SubnetRaw = subnetRaw ?? "";
            SubnetDisplay = DnsPolicyService.ResolveSubnetNames(SubnetRaw);
            Scope = scope ?? "";
        }
    }

    /// <summary>
    /// Операции с политиками разрешения запросов (Get/Add/Remove-DnsServerQueryResolutionPolicy)
    /// поверх IDnsCommandRunner - без WinForms-контролов, тестируется через фейковый раннер.
    /// </summary>
    public sealed class DnsPolicyService
    {
        private readonly IDnsCommandRunner _runner;

        public DnsPolicyService(IDnsCommandRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        /// <summary>Проверка полей перед созданием политики; null = всё заполнено.</summary>
        public static string ValidateAdd(string zoneName, string policyName, string subnetInput, string scopeName)
        {
            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(policyName) ||
                string.IsNullOrEmpty(subnetInput) || string.IsNullOrEmpty(scopeName))
                return "Заполни зону, имя политики, подсеть(и) и scope.";
            return null;
        }

        /// <summary>Разбирает поле "подсеть(и) через запятую" в чистые имена (пробелы и пустые куски отбрасываются).</summary>
        public static string[] ParseSubnetNames(string subnetInput)
        {
            return (subnetInput ?? "")
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }

        /// <summary>Значение параметра -ClientSubnet: "EQ," + имена через запятую (логика "любая из подсетей").</summary>
        public static string BuildClientSubnetValue(string[] subnetNames)
        {
            return "EQ," + string.Join(",", subnetNames);
        }

        /// <summary>
        /// ClientSubnet у политики выглядит как "EQ,net_100,Old_DNS_redirect13" - отсекаем
        /// оператор (EQ/NE), оставляем ЧИСТЫЕ имена подсетей без CIDR в скобках. Раньше здесь
        /// рядом с именем подставлялся реальный CIDR - выглядело удобно для чтения, но именно
        /// это "(10.0.1.0/24)" в скобках НЕ часть имени подсети, и при копипасте в поле
        /// "Подсети" диалога создания политики ловилась ошибка ("такой подсети не существует").
        /// CIDR теперь показывается только на вкладке "Подсети", где он и должен быть виден.
        /// </summary>
        public static string ResolveSubnetNames(string rawClientSubnet)
        {
            if (string.IsNullOrEmpty(rawClientSubnet)) return "";

            var tokens = rawClientSubnet.Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0 &&
                            !t.Equals("EQ", StringComparison.OrdinalIgnoreCase) &&
                            !t.Equals("NE", StringComparison.OrdinalIgnoreCase));

            return string.Join(", ", tokens);
        }

        /// <summary>
        /// Имя политики при дублировании. Имя уникально В ПРЕДЕЛАХ ЗОНЫ: если целей несколько
        /// в одной зоне (или цель - та же зона, что и у оригинала), к базовому имени добавляем
        /// "_&lt;scope&gt;", иначе в одной зоне было бы две политики с одинаковым именем.
        /// </summary>
        public static string DuplicateName(string baseName, bool keepExactName,
            IReadOnlyList<PolicyTarget> targets, string targetZone, string targetScope, string srcZone)
        {
            var sameZoneCount = targets.Count(x => x.Zone.Equals(targetZone, StringComparison.OrdinalIgnoreCase));
            var needSuffix = !keepExactName
                             || sameZoneCount > 1
                             || targetZone.Equals(srcZone, StringComparison.OrdinalIgnoreCase);
            return needSuffix ? $"{baseName}_{targetScope}" : baseName;
        }

        /// <summary>
        /// Политики зоны. Реальные имена свойств у Get-DnsServerQueryResolutionPolicy - "Criteria"
        /// (подсеть) и "Content" (scope), а не "ClientSubnet"/"ZoneScope" (это имена параметров у
        /// Add-DnsServerQueryResolutionPolicy, но в возвращаемом объекте они называются иначе).
        /// Если у какой-то политики подсеть/scope не удалось достать обычным способом, в
        /// Diagnostics попадает дамп ВСЕХ её полей - чтобы видеть точные имена и поправить код.
        /// </summary>
        public async Task<(List<DnsPolicy> Items, List<string> Diagnostics, string Log)> GetZonePoliciesAsync(string zoneName)
        {
            var parameters = new Dictionary<string, object> { ["ZoneName"] = zoneName };
            var (results, log) = await Task.Run(() => _runner.Invoke("Get-DnsServerQueryResolutionPolicy", parameters));

            var items = new List<DnsPolicy>();
            var diagnostics = new List<string>();
            foreach (var p in results)
            {
                var name = p.Properties["Name"]?.Value?.ToString() ?? "";
                var subnetRaw = DnsHelper.FlattenPropertyValue(p.Properties["Criteria"]?.Value);
                var scope = DnsHelper.FlattenPropertyValue(p.Properties["Content"]?.Value);
                items.Add(new DnsPolicy(name, subnetRaw, scope));

                if (string.IsNullOrEmpty(subnetRaw) || string.IsNullOrEmpty(scope))
                    diagnostics.Add($"(диагностика) все поля политики '{name}': " +
                                    string.Join("  |  ", p.Properties.Select(pr => $"{pr.Name}={DnsHelper.FlattenPropertyValue(pr.Value)}")));
            }
            return (items, diagnostics, log);
        }

        /// <summary>
        /// Создаёт политику "подсети -> scope". Синтаксис ZoneScope - "&lt;scope&gt;,&lt;вес&gt;":
        /// здесь базовый вариант "всё в один scope" с весом 1; для раскидывания трафика по
        /// нескольким scope добавляются ещё пары через запятую, например "ScopeA,1,ScopeB,1" (50/50).
        /// </summary>
        public async Task<(bool Success, string Log)> AddAsync(string zoneName, string policyName, string[] subnetNames, string scopeName)
        {
            var parameters = new Dictionary<string, object>
            {
                ["Name"] = policyName,
                ["Action"] = "ALLOW",
                ["ZoneName"] = zoneName,
                ["ClientSubnet"] = BuildClientSubnetValue(subnetNames),
                ["ZoneScope"] = $"{scopeName},1"
            };
            var (_, log) = await Task.Run(() => _runner.Invoke("Add-DnsServerQueryResolutionPolicy", parameters));
            return (DnsOutcome.WasSuccess(log), log);
        }

        public async Task<(bool Success, string Log)> RemoveAsync(string zoneName, string policyName)
        {
            var parameters = new Dictionary<string, object> { ["Name"] = policyName, ["ZoneName"] = zoneName, ["Force"] = true };
            var (_, log) = await Task.Run(() => _runner.Invoke("Remove-DnsServerQueryResolutionPolicy", parameters));
            return (DnsOutcome.WasSuccess(log), log);
        }
    }
}
