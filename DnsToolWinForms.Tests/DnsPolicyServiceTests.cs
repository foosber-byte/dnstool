using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DnsToolWinForms.Services;
using Xunit;

namespace DnsToolWinForms.Tests
{
    public class DnsPolicyServiceTests
    {
        [Fact]
        public void ResolveSubnetNames_StripsOperatorAndJoins()
        {
            Assert.Equal("net_100, Old_DNS_redirect13",
                DnsPolicyService.ResolveSubnetNames("EQ,net_100,Old_DNS_redirect13"));
        }

        [Theory]
        [InlineData("NE,net_a, net_b")]
        [InlineData("eq, net_a ,net_b")]
        public void ResolveSubnetNames_OperatorCaseInsensitiveAndTrimmed(string raw)
        {
            Assert.Equal("net_a, net_b", DnsPolicyService.ResolveSubnetNames(raw));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("EQ")]
        [InlineData("EQ,,,")]
        public void ResolveSubnetNames_EmptyInput_EmptyResult(string raw)
        {
            Assert.Equal("", DnsPolicyService.ResolveSubnetNames(raw));
        }

        [Fact]
        public void ParseSubnetNames_SplitsTrimsAndDropsEmpty()
        {
            Assert.Equal(new[] { "net_100", "net_2", "net_b" },
                DnsPolicyService.ParseSubnetNames(" net_100 , net_2,, net_b, "));
        }

        [Fact]
        public void BuildClientSubnetValue_PrefixesWithEq()
        {
            Assert.Equal("EQ,net_100,net_b", DnsPolicyService.BuildClientSubnetValue(new[] { "net_100", "net_b" }));
        }

        [Fact]
        public void DnsPolicy_ComputesDisplayFromRaw()
        {
            var p = new DnsPolicy("pol", "EQ,net_a,net_b", "scope1");
            Assert.Equal("net_a, net_b", p.SubnetDisplay);
            Assert.Equal("scope1", p.Scope);
        }

        [Theory]
        [InlineData("", "pol", "net_a", "scope")]
        [InlineData("z", "", "net_a", "scope")]
        [InlineData("z", "pol", "", "scope")]
        [InlineData("z", "pol", "net_a", "")]
        public void ValidateAdd_AnyFieldEmpty_ReturnsMessage(string zone, string policy, string subnet, string scope)
        {
            Assert.NotNull(DnsPolicyService.ValidateAdd(zone, policy, subnet, scope));
        }

        [Fact]
        public void ValidateAdd_AllFilled_ReturnsNull()
        {
            Assert.Null(DnsPolicyService.ValidateAdd("z", "pol", "net_a", "scope"));
        }

        private static List<PolicyTarget> Targets(params (string Zone, string Scope)[] pairs) =>
            pairs.Select(x => new PolicyTarget { Zone = x.Zone, Scope = x.Scope }).ToList();

        [Fact]
        public void DuplicateName_SameZoneAsSource_AlwaysSuffix()
        {
            var targets = Targets(("corp.local", "scopeB"));
            // Даже с галочкой "оставить имя как есть" в зоне оригинала имя занято самим оригиналом.
            Assert.Equal("pol_scopeB",
                DnsPolicyService.DuplicateName("pol", keepExactName: true, targets, "corp.local", "scopeB", "corp.local"));
        }

        [Fact]
        public void DuplicateName_SeveralScopesOfOneZone_SuffixEach()
        {
            var targets = Targets(("other.local", "s1"), ("other.local", "s2"));
            Assert.Equal("other_s1", DnsPolicyService.DuplicateName("other", true, targets, "other.local", "s1", "corp.local"));
            Assert.Equal("other_s2", DnsPolicyService.DuplicateName("other", true, targets, "other.local", "s2", "corp.local"));
        }

        [Fact]
        public void DuplicateName_KeepExactNameSingleTargetOtherZone_ExactName()
        {
            var targets = Targets(("other.local", "scopeB"));
            Assert.Equal("pol",
                DnsPolicyService.DuplicateName("pol", keepExactName: true, targets, "other.local", "scopeB", "corp.local"));
        }

        [Fact]
        public void DuplicateName_NoKeepExactName_AlwaysSuffix()
        {
            var targets = Targets(("other.local", "scopeB"));
            Assert.Equal("pol_scopeB",
                DnsPolicyService.DuplicateName("pol", keepExactName: false, targets, "other.local", "scopeB", "corp.local"));
        }

        [Fact]
        public async Task GetZonePoliciesAsync_ParsesCriteriaAndContent()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(new List<System.Management.Automation.PSObject>
            {
                TestObjects.Obj(("Name", "pol1"), ("Criteria", "EQ,net_100"), ("Content", "scope1")),
                TestObjects.Obj(("Name", "pol2"), ("Criteria", ""), ("Content", "")),
            });

            var (items, diagnostics, log) = await new DnsPolicyService(runner).GetZonePoliciesAsync("corp.local");

            Assert.Equal(2, items.Count);
            Assert.Equal("pol1", items[0].Name);
            Assert.Equal("net_100", items[0].SubnetDisplay);
            Assert.Equal("scope1", items[0].Scope);
            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Get-DnsServerQueryResolutionPolicy", cmdlet);
            Assert.Equal("corp.local", parameters["ZoneName"]);
            Assert.StartsWith("OK:", log);
            // У pol2 не достали подсеть/scope - должен быть дамп всех полей
            var dump = Assert.Single(diagnostics);
            Assert.Contains("все поля политики 'pol2'", dump);
        }

        [Fact]
        public async Task AddAsync_PassesEqSubnetsAndWeightedScope()
        {
            var runner = new FakeDnsCommandRunner();

            var (success, _) = await new DnsPolicyService(runner)
                .AddAsync("corp.local", "pol", new[] { "net_a", "net_b" }, "scope1");

            Assert.True(success);
            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Add-DnsServerQueryResolutionPolicy", cmdlet);
            Assert.Equal("pol", parameters["Name"]);
            Assert.Equal("ALLOW", parameters["Action"]);
            Assert.Equal("corp.local", parameters["ZoneName"]);
            Assert.Equal("EQ,net_a,net_b", parameters["ClientSubnet"]);
            Assert.Equal("scope1,1", parameters["ZoneScope"]);
        }

        [Fact]
        public async Task RemoveAsync_ForcesByNameAndZone()
        {
            var runner = new FakeDnsCommandRunner();

            var (success, _) = await new DnsPolicyService(runner).RemoveAsync("corp.local", "pol");

            Assert.True(success);
            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Remove-DnsServerQueryResolutionPolicy", cmdlet);
            Assert.Equal("pol", parameters["Name"]);
            Assert.Equal("corp.local", parameters["ZoneName"]);
            Assert.Equal(true, parameters["Force"]);
        }
    }
}
