using System;
using System.Collections.Generic;
using System.Linq;
using DnsToolWinForms.Services;
using Xunit;

namespace DnsToolWinForms.Tests
{
    public class ReverseDnsServiceTests
    {
        // ---------- GetReverseZoneName ----------

        [Theory]
        [InlineData("192.168.10.5", "5.10.168.192.in-addr.arpa")]
        [InlineData("10.0.1.10", "10.1.0.10.in-addr.arpa")]
        [InlineData("1.2.3.4", "4.3.2.1.in-addr.arpa")]
        public void GetReverseZoneName_Ipv4_ReversesOctets(string ip, string expected)
        {
            Assert.Equal(expected, ReverseDnsService.GetReverseZoneName(ip));
        }

        [Fact]
        public void GetReverseZoneName_Ipv6_BuildsNibbleChain()
        {
            // 2001:db8::1 -> полная запись 1.0.0.0. ... 8.b.d.0.1.0.0.2.ip6.arpa
            var full = ReverseDnsService.GetReverseZoneName("2001:db8::1");
            Assert.EndsWith(".ip6.arpa", full);
            Assert.StartsWith("1.0.0.0.", full);           // последний байт адреса - первым в имени
            Assert.EndsWith("8.b.d.0.1.0.0.2.ip6.arpa", full);
        }

        [Theory]
        [InlineData("")]
        [InlineData("не-ip")]
        [InlineData("999.1.1.1")]
        public void GetReverseZoneName_Invalid_ReturnsNull(string ip)
        {
            Assert.Null(ReverseDnsService.GetReverseZoneName(ip));
        }

        // ---------- GetReverseZoneCandidates ----------

        [Fact]
        public void GetReverseZoneCandidates_Ipv4_LongestToShortest()
        {
            var candidates = ReverseDnsService.GetReverseZoneCandidates("5.10.168.192.in-addr.arpa").ToList();
            Assert.Equal(new[]
            {
                "5.10.168.192.in-addr.arpa", // /32-зона (редкая, но допустимая)
                "10.168.192.in-addr.arpa",   // /24
                "168.192.in-addr.arpa",      // /16
                "192.in-addr.arpa"           // /8
            }, candidates);
        }

        [Fact]
        public void GetReverseZoneCandidates_NonReverse_ReturnsEmpty()
        {
            Assert.Empty(ReverseDnsService.GetReverseZoneCandidates("corp.local"));
        }

        // ---------- BuildPtrName ----------

        [Theory]
        [InlineData("5.10.168.192.in-addr.arpa", "10.168.192.in-addr.arpa", "5")]
        [InlineData("5.10.168.192.in-addr.arpa", "168.192.in-addr.arpa", "5.10")]
        [InlineData("5.10.168.192.in-addr.arpa", "192.in-addr.arpa", "5.10.168")]
        [InlineData("5.10.168.192.in-addr.arpa", "corp.local", null)] // несуразная зона - null
        public void BuildPtrName_StripsZoneSuffix(string full, string zone, string expected)
        {
            Assert.Equal(expected, ReverseDnsService.BuildPtrName(full, zone));
        }

        // ---------- IsSpecialPurposeAddress ----------

        [Theory]
        [InlineData("127.0.0.1", true)]     // loopback
        [InlineData("169.254.1.5", true)]   // APIPA
        [InlineData("224.0.0.1", true)]     // multicast
        [InlineData("255.255.255.255", true)] // broadcast
        [InlineData("100.64.0.1", true)]    // CGNAT
        [InlineData("192.168.10.5", false)] // обычный частный - создаём
        [InlineData("10.0.1.10", false)]
        [InlineData("8.8.8.8", false)]
        public void IsSpecialPurposeAddress_Ipv4(string ip, bool special)
        {
            Assert.Equal(special, ReverseDnsService.IsSpecialPurposeAddress(ip));
        }

        [Fact]
        public void IsSpecialPurposeAddress_Ipv6_LinkLocalAndMulticast()
        {
            Assert.True(ReverseDnsService.IsSpecialPurposeAddress("fe80::1"));    // link-local
            Assert.True(ReverseDnsService.IsSpecialPurposeAddress("ff02::1"));    // multicast
            Assert.True(ReverseDnsService.IsSpecialPurposeAddress("::1"));        // loopback
            Assert.False(ReverseDnsService.IsSpecialPurposeAddress("2001:db8::1"));
        }

        // ---------- BackfillPtrsAsync (интеграционный, через FakeDnsCommandRunner) ----------

        [Fact]
        public async System.Threading.Tasks.Task Backfill_CreatesMissingPtr_ReportsExistingAndMissingZone()
        {
            var runner = new FakeDnsCommandRunner();

            // 1) Записи scope: две A с адресами одной сети /24 + одна MX (не адресная).
            var records = new List<System.Management.Automation.PSObject>
            {
                TestObjects.ResourceRecordA("srv1", "192.168.10.5"),
                TestObjects.ResourceRecordA("srv2", "192.168.10.6"),
                TestObjects.ResourceRecordA("srv3", "192.168.10.7")
            };
            // 2) Зоны сервера: прямая + обратная /24 - и PTR для srv1 уже существует.
            var zones = new List<System.Management.Automation.PSObject>
            {
                TestObjects.Zone("corp.local", "Primary"),
                TestObjects.Zone("10.168.192.in-addr.arpa", "Primary")
            };
            var existingPtrs = new List<System.Management.Automation.PSObject>
            {
                TestObjects.ResourceRecordPtr("5", "srv1.corp.local")
            };

            runner.Enqueue(records);                                   // Get-DnsServerResourceRecord (scope)
            runner.Enqueue(zones);                                     // Get-DnsServerZone
            runner.Enqueue(existingPtrs);                              // Get-DnsServerResourceRecord (PTR reverse-зоны)
            runner.Enqueue(new List<System.Management.Automation.PSObject>());  // Add PTR для srv2 (пусто + OK-лог)
            runner.Enqueue(new List<System.Management.Automation.PSObject>());  // Add PTR для srv3

            var svc = new ReverseDnsService(runner);
            var created = new List<(string Zone, string Name, string Fqdn)>();
            var progressReports = new List<int>();

            var rows = await svc.BackfillPtrsAsync("corp.local", "s1",
                pct => progressReports.Add(pct),
                _ => { },
                null,
                (z, n, f) => created.Add((z, n, f)));

            Assert.Equal(3, rows.Count);
            Assert.Contains(rows, r => r.RecordName == "srv1" && r.Status == "Уже есть");
            Assert.Equal(2, created.Count);                            // srv2 и srv3 созданы
            Assert.All(created, c => Assert.Equal("10.168.192.in-addr.arpa", c.Zone));
            Assert.Contains(created, c => c.Name == "6" && c.Fqdn == "srv2.corp.local");

            // Командлет создания PTR получил ровно нужные параметры.
            var addCalls = runner.Calls.Where(c => c.Cmdlet == "Add-DnsServerResourceRecordPtr").ToList();
            Assert.Equal(2, addCalls.Count);
            var srv3Call = addCalls.Single(c => (string)c.Parameters["Name"] == "7");
            Assert.Equal("10.168.192.in-addr.arpa", srv3Call.Parameters["ZoneName"]);
            Assert.Equal("srv3.corp.local", srv3Call.Parameters["PtrDomainName"]);

            // Прогресс дошёл до 100%.
            Assert.Equal(100, progressReports.Last());
        }

        [Fact]
        public async System.Threading.Tasks.Task Backfill_NoReverseZone_ReportedNotCreated()
        {
            var runner = new FakeDnsCommandRunner();

            runner.Enqueue(new List<System.Management.Automation.PSObject>
            {
                TestObjects.ResourceRecordA("host1", "192.168.10.5")
            });
            runner.Enqueue(new List<System.Management.Automation.PSObject>
            {
                TestObjects.Zone("corp.local", "Primary") // обратной зоны нет
            });

            var svc = new ReverseDnsService(runner);
            var rows = await svc.BackfillPtrsAsync("corp.local", "s1", _ => { }, _ => { });

            var row = Assert.Single(rows);
            Assert.Equal("Нет reverse-зоны", row.Status);
            Assert.Equal("5.10.168.192.in-addr.arpa", row.RequiredZone);
            Assert.DoesNotContain(runner.Calls, c => c.Cmdlet == "Add-DnsServerResourceRecordPtr");
        }
    }
}
