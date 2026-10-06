using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DnsToolWinForms.Services;
using Xunit;

namespace DnsToolWinForms.Tests
{
    public class DnsSubnetServiceTests
    {
        [Fact]
        public async Task GetAllAsync_ParsesNameAndIpv4Subnet()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(new List<System.Management.Automation.PSObject>
            {
                TestObjects.Obj(("Name", "net_100"), ("IPv4Subnet", "10.0.100.0/24")),
            });

            var (items, _) = await new DnsSubnetService(runner).GetAllAsync();

            var subnet = Assert.Single(items);
            Assert.Equal("net_100", subnet.Name);
            Assert.Equal("10.0.100.0/24", subnet.Cidr);
            Assert.Contains("net_100", subnet.Display);
            Assert.Contains("10.0.100.0/24", subnet.Display);
        }

        [Fact]
        public async Task GetAllAsync_FallsBackToIpv6Subnet()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(new List<System.Management.Automation.PSObject>
            {
                TestObjects.Obj(("Name", "v6"), ("IPv4Subnet", ""), ("IPv6Subnet", "2001:db8::/64")),
            });

            var (items, _) = await new DnsSubnetService(runner).GetAllAsync();

            Assert.Equal("2001:db8::/64", Assert.Single(items).Cidr);
        }

        [Theory]
        [InlineData("", "10.0.0.0/8")]
        [InlineData("net", "")]
        [InlineData(null, "10.0.0.0/8")]
        public void ValidateAdd_MissingFields_ReturnsError(string name, string cidr)
        {
            Assert.NotNull(DnsSubnetService.ValidateAdd(name, cidr));
        }

        [Fact]
        public void ValidateAdd_FilledFields_ReturnsNull()
        {
            Assert.Null(DnsSubnetService.ValidateAdd("net_100", "10.0.100.0/24"));
        }

        [Fact]
        public async Task AddAsync_SendsNameAndCidr_AndMapsSuccess()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(null, "OK: подсеть создана");

            var (success, log) = await new DnsSubnetService(runner).AddAsync("net_100", "10.0.100.0/24");

            Assert.True(success);
            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Add-DnsServerClientSubnet", cmdlet);
            Assert.Equal("net_100", parameters["Name"]);
            Assert.Equal("10.0.100.0/24", parameters["IPv4Subnet"]);
        }

        [Fact]
        public async Task AddAsync_ErrorLog_MapsToFailure()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(null, "ОШИБКА: подсеть уже существует");

            var (success, _) = await new DnsSubnetService(runner).AddAsync("net", "10.0.0.0/8");

            Assert.False(success);
        }

        [Fact]
        public async Task RemoveAsync_SendsNameAndForce()
        {
            var runner = new FakeDnsCommandRunner();
            runner.Enqueue(null, "OK: удалено");

            await new DnsSubnetService(runner).RemoveAsync("net_100");

            var (cmdlet, parameters) = Assert.Single(runner.Calls);
            Assert.Equal("Remove-DnsServerClientSubnet", cmdlet);
            Assert.Equal("net_100", parameters["Name"]);
            Assert.Equal(true, parameters["Force"]);
        }
    }

    public class CoreServiceTests
    {
        [Theory]
        [InlineData("OK: что-то сделано", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("ОШИБКА: отказ", false)]
        [InlineData("ИСКЛЮЧЕНИЕ при вызове", false)]
        public void WasSuccess_MapsLogText(string log, bool expected)
        {
            Assert.Equal(expected, DnsOutcome.WasSuccess(log));
        }

        [Fact]
        public void ServerContext_Set_RaisesEventOnlyOnChange()
        {
            var context = new ServerContext(new FakeDnsCommandRunner());
            var changes = 0;
            context.CurrentServerChanged += (s, e) => changes++;

            context.Set("server1");
            context.Set("server1"); // то же значение - события быть не должно
            context.Set("");

            Assert.Equal(2, changes);
            Assert.True(context.IsLocal);
            Assert.Equal("", context.CurrentServer);
        }

        [Fact]
        public void ServerContext_TemporarySwitch_RestoresPreviousValue()
        {
            var context = new ServerContext(new FakeDnsCommandRunner());
            context.Set("main");

            using (context.BeginTemporaryServer("temp"))
            {
                Assert.Equal("temp", context.CurrentServer);
            }

            Assert.Equal("main", context.CurrentServer);
        }

        [Fact]
        public void ServerContext_TemporarySwitch_RestoresAfterException()
        {
            var context = new ServerContext(new FakeDnsCommandRunner());
            context.Set("main");

            try
            {
                using (context.BeginTemporaryServer("temp"))
                {
                    throw new System.InvalidOperationException("упс");
                }
            }
            catch (InvalidOperationException) { }

            Assert.Equal("main", context.CurrentServer);
        }
    }
}
