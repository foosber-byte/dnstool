using System.Collections.Generic;
using System.Threading.Tasks;
using DnsToolWinForms.Services;
using Xunit;

namespace DnsToolWinForms.Tests
{
    public class DnsScopeServiceTests
    {
        [Fact]
        public async Task GetAllZonesAsync_InvokesGetDnsServerZoneWithoutParams()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(new List<System.Management.Automation.PSObject> { TestObjects.Zone("corp.local", "Primary") });

            var (results, log) = await new DnsScopeService(runner).GetAllZonesAsync();

            Assert.Single(results);
            Assert.StartsWith("OK:", log);
            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Get-DnsServerZone", cmdlet);
            Assert.Null(parameters);
        }

        [Fact]
        public async Task GetZoneAsync_PassesZoneName()
        {
            var runner = new FakeDnsCommandRunner();

            await new DnsScopeService(runner).GetZoneAsync("corp.local");

            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Get-DnsServerZone", cmdlet);
            Assert.Equal("corp.local", parameters["Name"]);
        }

        [Theory]
        [InlineData("Domain", null)]   // AD, реплика на домен
        [InlineData("Forest", null)]   // AD, реплика на лес
        public async Task AddPrimaryZoneAsync_AdZone_SetsReplicationScopeNoFile(string replicationScope, string zoneFile)
        {
            var runner = new FakeDnsCommandRunner();

            await new DnsScopeService(runner).AddPrimaryZoneAsync("corp.local", replicationScope, zoneFile);

            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Add-DnsServerPrimaryZone", cmdlet);
            Assert.Equal("corp.local", parameters["Name"]);
            Assert.Equal(replicationScope, parameters["ReplicationScope"]);
            Assert.False(parameters.ContainsKey("ZoneFile"));
        }

        [Fact]
        public async Task AddPrimaryZoneAsync_FileZone_SetsZoneFileNoReplicationScope()
        {
            var runner = new FakeDnsCommandRunner();

            await new DnsScopeService(runner).AddPrimaryZoneAsync("corp.local", null, "corp.local.dns");

            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Add-DnsServerPrimaryZone", cmdlet);
            Assert.Equal("corp.local.dns", parameters["ZoneFile"]);
            Assert.False(parameters.ContainsKey("ReplicationScope"));
        }

        [Fact]
        public async Task RemoveZoneAsync_ForcesByName()
        {
            var runner = new FakeDnsCommandRunner();

            var (success, _) = await new DnsScopeService(runner).RemoveZoneAsync("corp.local");

            Assert.True(success);
            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Remove-DnsServerZone", cmdlet);
            Assert.Equal("corp.local", parameters["Name"]);
            Assert.Equal(true, parameters["Force"]);
        }

        [Fact]
        public async Task AddScopeAsync_PassesZoneAndScopeName()
        {
            var runner = new FakeDnsCommandRunner();

            await new DnsScopeService(runner).AddScopeAsync("corp.local", "scope1");

            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Add-DnsServerZoneScope", cmdlet);
            Assert.Equal("corp.local", parameters["ZoneName"]);
            Assert.Equal("scope1", parameters["Name"]);
            Assert.False(parameters.ContainsKey("Force"));
        }

        [Fact]
        public async Task RemoveScopeAsync_ForcesByZoneAndScopeName()
        {
            var runner = new FakeDnsCommandRunner();

            var (success, _) = await new DnsScopeService(runner).RemoveScopeAsync("corp.local", "scope1");

            Assert.True(success);
            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Remove-DnsServerZoneScope", cmdlet);
            Assert.Equal("corp.local", parameters["ZoneName"]);
            Assert.Equal("scope1", parameters["Name"]);
            Assert.Equal(true, parameters["Force"]);
        }

        [Fact]
        public async Task Operations_FailedLog_ReturnsFalse()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(new List<System.Management.Automation.PSObject>(), "ОШИБКА: 9611");

            var (success, _) = await new DnsScopeService(runner).AddScopeAsync("corp.local", "scope1");

            Assert.False(success);
        }
    }
}
