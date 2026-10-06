using System.Collections.Generic;
using System.Threading.Tasks;
using DnsToolWinForms.Services;
using Xunit;

namespace DnsToolWinForms.Tests
{
    public class DnsZoneServiceTests
    {
        [Fact]
        public void IsScopeCapableZone_PrimaryZone_True()
        {
            Assert.True(DnsZoneService.IsScopeCapableZone(TestObjects.Zone("corp.local", "Primary")));
        }

        [Theory]
        [InlineData("Forwarder")]
        [InlineData("Stub")]
        public void IsScopeCapableZone_ConditionalForwarderAndStub_False(string zoneType)
        {
            Assert.False(DnsZoneService.IsScopeCapableZone(TestObjects.Zone("fwd.example.com", zoneType)));
        }

        [Theory]
        [InlineData("TrustAnchors", "Primary")]
        [InlineData(".", "Primary")]
        [InlineData("0.in-addr.arpa", "Primary")]
        [InlineData("127.in-addr.arpa", "Primary")]
        [InlineData("255.in-addr.arpa", "Primary")]
        [InlineData("cache.zone", "Cache")]
        public void IsScopeCapableZone_ServiceAutoZones_False(string name, string zoneType)
        {
            Assert.False(DnsZoneService.IsScopeCapableZone(TestObjects.Zone(name, zoneType)));
        }

        [Fact]
        public void IsScopeCapableZone_MarkedAutoCreated_False()
        {
            Assert.False(DnsZoneService.IsScopeCapableZone(TestObjects.Zone("some.zone", "Primary", isAutoCreated: true)));
        }

        [Fact]
        public void IsScopeCapableZone_NullZone_False()
        {
            Assert.False(DnsZoneService.IsScopeCapableZone(null));
        }

        [Fact]
        public async Task GetScopeCapableZoneNamesAsync_FiltersAndKeepsServerOrder()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(new List<System.Management.Automation.PSObject>
            {
                TestObjects.Zone("corp.local", "Primary"),
                TestObjects.Zone("fwd.example.com", "Forwarder"),
                TestObjects.Zone("stub.example.com", "Stub"),
                TestObjects.Zone("TrustAnchors", "Primary"),
                TestObjects.Zone("10.in-addr.arpa", "Primary"),
            });

            var (names, log) = await new DnsZoneService(runner).GetScopeCapableZoneNamesAsync();

            Assert.Equal(new[] { "corp.local", "10.in-addr.arpa" }, names);
            Assert.StartsWith("OK:", log);
            var (cmdlet, _) = Assert.Single(runner.Calls);
            Assert.Equal("Get-DnsServerZone", cmdlet);
        }

        [Fact]
        public async Task GetZoneScopeNamesAsync_PrefersZoneScopeProperty()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(new List<System.Management.Automation.PSObject>
            {
                TestObjects.Obj(("ZoneScope", "scopeA")),
                TestObjects.Obj(("ZoneScope", "scopeB")),
            });

            var (names, _) = await new DnsZoneService(runner).GetZoneScopeNamesAsync("corp.local");

            Assert.Equal(new[] { "scopeA", "scopeB" }, names);
            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Get-DnsServerZoneScope", cmdlet);
            Assert.Equal("corp.local", parameters["ZoneName"]);
        }

        [Fact]
        public async Task GetZoneScopeNamesAsync_FallsBackToNameProperty()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(new List<System.Management.Automation.PSObject>
            {
                TestObjects.Obj(("Name", "onlyName")),
            });

            var (names, _) = await new DnsZoneService(runner).GetZoneScopeNamesAsync("z");

            Assert.Equal(new[] { "onlyName" }, names);
        }

        [Fact]
        public async Task GetClientSubnetNamesAsync_SortsCaseInsensitively()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(new List<System.Management.Automation.PSObject>
            {
                TestObjects.Obj(("Name", "net_B")),
                TestObjects.Obj(("Name", "net_a")),
                TestObjects.Obj(("Name", "Net_C")),
            });

            var (names, _) = await new DnsZoneService(runner).GetClientSubnetNamesAsync();

            Assert.Equal(new[] { "net_a", "net_B", "Net_C" }, names);
        }
    }
}
