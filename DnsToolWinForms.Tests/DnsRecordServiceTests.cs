using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using DnsToolWinForms.Services;
using Xunit;

namespace DnsToolWinForms.Tests
{
    /// <summary>Тесты чистой логики записей (Services/DnsRecordService.cs) - без UI и без PowerShell.</summary>
    public class DnsRecordServiceTests
    {
        // ---------- NormalizeRecordName ----------

        [Theory]
        [InlineData("www.corp.local", "corp.local", "www")]          // полное имя - зона срезается
        [InlineData("bla.bla.corp.local", "corp.local", "bla.bla")]  // многоуровневое - срезается только зона
        [InlineData("www", "corp.local", "www")]                     // относительное - как есть
        [InlineData("corp.local", "corp.local", "@")]                // само имя зоны - корень зоны
        [InlineData("abs.example.net.", "corp.local", "abs.example.net.")] // абсолютный FQDN - не трогаем
        [InlineData("@", "corp.local", "@")]
        [InlineData("", "corp.local", "")]
        [InlineData("www", "", "www")]                               // зона не выбрана - не трогаем
        public void NormalizeRecordName_Cases(string name, string zone, string expected)
        {
            Assert.Equal(expected, DnsRecordService.NormalizeRecordName(name, zone));
        }

        // ---------- BuildAddRecordCommand ----------

        [Fact]
        public void BuildAddRecordCommand_A_UsesDedicatedCmdlet()
        {
            var (cmdlet, p) = DnsRecordService.BuildAddRecordCommand("z", "s", "A", "www", "10.0.0.1", "", "", "");
            Assert.Equal("Add-DnsServerResourceRecordA", cmdlet);
            Assert.Equal("z", p["ZoneName"]);
            Assert.Equal("s", p["ZoneScope"]);
            Assert.Equal("www", p["Name"]);
            Assert.Equal("10.0.0.1", p["IPv4Address"]);
        }

        [Theory]
        [InlineData("AAAA", "Add-DnsServerResourceRecordAAAA", "IPv6Address")]
        [InlineData("CNAME", "Add-DnsServerResourceRecordCName", "HostNameAlias")]
        [InlineData("PTR", "Add-DnsServerResourceRecordPtr", "PtrDomainName")]
        public void BuildAddRecordCommand_SimpleTypes(string type, string expectedCmdlet, string valueKey)
        {
            var (cmdlet, p) = DnsRecordService.BuildAddRecordCommand("z", "s", type, "n", "v", "", "", "");
            Assert.Equal(expectedCmdlet, cmdlet);
            Assert.Equal("v", p[valueKey]);
        }

        [Fact]
        public void BuildAddRecordCommand_MX_HasPreference()
        {
            var (cmdlet, p) = DnsRecordService.BuildAddRecordCommand("z", "s", "MX", "n", "mail.x", "20", "", "");
            Assert.Equal("Add-DnsServerResourceRecordMX", cmdlet);
            Assert.Equal("mail.x", p["MailExchange"]);
            Assert.Equal((ushort)20, p["Preference"]);
        }

        [Fact]
        public void BuildAddRecordCommand_NS_UniversalCmdletWithFlag()
        {
            var (cmdlet, p) = DnsRecordService.BuildAddRecordCommand("z", "s", "NS", "n", "ns1.x", "", "", "");
            Assert.Equal("Add-DnsServerResourceRecord", cmdlet);
            Assert.Equal(true, p["NS"]);
            Assert.Equal("ns1.x", p["NameServer"]);
        }

        [Fact]
        public void BuildAddRecordCommand_TXT_UniversalCmdletWithFlag()
        {
            var (cmdlet, p) = DnsRecordService.BuildAddRecordCommand("z", "s", "TXT", "n", "v=spf1", "", "", "");
            Assert.Equal("Add-DnsServerResourceRecord", cmdlet);
            Assert.Equal(true, p["Txt"]);
            Assert.Equal("v=spf1", p["DescriptiveText"]);
        }

        [Fact]
        public void BuildAddRecordCommand_SRV_ParsesNumbersWithFallbacks()
        {
            var (cmdlet, p) = DnsRecordService.BuildAddRecordCommand("z", "s", "SRV", "n", "sip.x", "5", "не число", "");
            Assert.Equal("Add-DnsServerResourceRecord", cmdlet);
            Assert.Equal(true, p["Srv"]);
            Assert.Equal("sip.x", p["DomainName"]);
            Assert.Equal((ushort)5, p["Priority"]);
            Assert.Equal((ushort)10, p["Weight"]);   // fallback 10
            Assert.Equal((ushort)443, p["Port"]);    // fallback 443
        }

        // ---------- ParseIntOrDefault / EnsureTrailingDot ----------

        [Theory]
        [InlineData("25", 0, 25)]
        [InlineData("abc", 7, 7)]
        [InlineData("", 9, 9)]
        [InlineData(null, 3, 3)]
        public void ParseIntOrDefault_Cases(string s, int fallback, int expected)
        {
            Assert.Equal(expected, DnsRecordService.ParseIntOrDefault(s, fallback));
        }

        [Theory]
        [InlineData("a.b", "a.b.")]
        [InlineData("a.b.", "a.b.")]
        [InlineData("", "")]
        [InlineData(null, null)]
        public void EnsureTrailingDot_Cases(string fqdn, string expected)
        {
            Assert.Equal(expected, DnsRecordService.EnsureTrailingDot(fqdn));
        }

        // ---------- BuildRecordTree / CountRecordsRecursive / GetFolderPathSuffix ----------

        private static PSObject Record(string hostName)
        {
            return TestObjects.Obj(("HostName", hostName), ("RecordType", "A"));
        }

        [Fact]
        public void BuildRecordTree_GroupsByLabelsFromRightToLeft()
        {
            var root = DnsRecordService.BuildRecordTree(new List<PSObject>
            {
                Record("@"),
                Record("admin.pro32connect"),
                Record("_ldap._tcp.dc._msdcs"),
            });

            // запись корня зоны - прямо в корне
            Assert.Single(root.RecordsHere);

            // admin.pro32connect -> папка pro32connect, внутри неё лист-узел admin с самой записью
            Assert.True(root.Children.ContainsKey("pro32connect"));
            Assert.Empty(root.Children["pro32connect"].RecordsHere);
            Assert.Single(root.Children["pro32connect"].Children["admin"].RecordsHere);

            // _ldap._tcp.dc._msdcs -> путь _msdcs > dc > _tcp > _ldap (правый сегмент - внешний)
            var ldap = root.Children["_msdcs"].Children["dc"].Children["_tcp"].Children["_ldap"];
            Assert.NotNull(ldap);
            Assert.Single(ldap.RecordsHere);

            Assert.Equal(3, DnsRecordService.CountRecordsRecursive(root));
        }

        [Fact]
        public void GetFolderPathSuffix_ConcatenatesLabelsFromNodeToRoot()
        {
            var root = new RecordTreeNode { Label = "" };
            var a = new RecordTreeNode { Label = "a", Parent = root };
            var b = new RecordTreeNode { Label = "b", Parent = a };

            // метки идут ОТ ГЛУБОКОГО узла К КОРНЮ scope (см. док-комментарий метода)
            Assert.Equal("b.a", DnsRecordService.GetFolderPathSuffix(b));
            Assert.Equal("a", DnsRecordService.GetFolderPathSuffix(a));
            Assert.Equal("", DnsRecordService.GetFolderPathSuffix(root)); // корень scope не включается
        }

        // ---------- разбор .dns-файла ----------

        [Theory]
        [InlineData("host IN A 1.2.3.4 ; коммент", "host IN A 1.2.3.4 ")]
        [InlineData("t IN TXT \"v=1; x\" ; после", "t IN TXT \"v=1; x\" ")] // ';' внутри кавычек не режет
        [InlineData("без комментария", "без комментария")]
        public void StripZoneFileComment_Cases(string line, string expected)
        {
            Assert.Equal(expected, DnsRecordService.StripZoneFileComment(line));
        }

        [Fact]
        public void TokenizeZoneFileLine_SplitsOnWhitespaceAndParens_KeepsQuotedText()
        {
            var tokens = DnsRecordService.TokenizeZoneFileLine("www\tIN\tA\t\"a b\"(x)");
            Assert.Equal(new[] { "www", "IN", "A", "\"a b\"", "x" }, tokens);
        }

        [Theory]
        [InlineData("3600", true)]
        [InlineData("1h", true)]
        [InlineData("30m", true)]
        [InlineData("2w", true)]
        [InlineData("10x", false)]
        [InlineData("h", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsZoneFileTtl_Cases(string token, bool expected)
        {
            Assert.Equal(expected, DnsRecordService.IsZoneFileTtl(token));
        }

        [Theory]
        [InlineData("IN", true)]
        [InlineData("in", true)]
        [InlineData("CH", true)]
        [InlineData("HS", true)]
        [InlineData("CS", true)]
        [InlineData("A", false)]
        public void IsZoneFileClass_Cases(string token, bool expected)
        {
            Assert.Equal(expected, DnsRecordService.IsZoneFileClass(token));
        }

        [Theory]
        [InlineData("@", "corp.local", "@")]
        [InlineData("corp.local", "corp.local", "@")]
        [InlineData("www.CORP.local.", "corp.local", "www")]
        [InlineData("www", "corp.local", "www")]
        [InlineData("www", "", "www")]
        public void NormalizeZoneOwner_Cases(string name, string zone, string expected)
        {
            Assert.Equal(expected, DnsRecordService.NormalizeZoneOwner(name, zone));
        }

        [Theory]
        [InlineData("Corp.Local.", "corp.local")]
        [InlineData("  zone  ", "zone")]
        [InlineData(null, "")]
        public void NormalizeZoneName_Cases(string s, string expected)
        {
            Assert.Equal(expected, DnsRecordService.NormalizeZoneName(s));
        }

        [Theory]
        [InlineData(" 10.0.0.1 ", "10.0.0.1")]
        [InlineData("FE80::1", "fe80::1")]
        [InlineData("NotAnIp.X", "notanip.x")]
        public void NormalizeZoneIp_Cases(string s, string expected)
        {
            Assert.Equal(expected, DnsRecordService.NormalizeZoneIp(s));
        }

        [Theory]
        [InlineData("10.0.0.1", "A", "A 10.0.0.1")]
        [InlineData("10 mail.example.com.", "MX", "MX 10 mail.example.com")]
        [InlineData("0 0 443 sip.corp.local.", "SRV", "SRV 0 0 443 sip.corp.local")]
        [InlineData("\"v=spf1 -all\"", "TXT", "TXT v=spf1 -all")]
        [InlineData("1 2", "SRV", null)]          // слишком мало токенов
        [InlineData("что-угодно", "SOA", null)]   // тип не поддерживается файловым удалением
        public void ZoneRdataFromFile_Cases(string rdata, string type, string expected)
        {
            Assert.Equal(expected, DnsRecordService.ZoneRdataFromFile(rdata, type));
        }

        [Theory]
        [InlineData("\"v=spf1\" \" ~all\"", "v=spf1 ~all")] // склейка кавычечных сегментов
        [InlineData("без кавычек", "без кавычек")]
        [InlineData("", "")]
        public void NormalizeZoneTxt_StringCases(string value, string expected)
        {
            Assert.Equal(expected, DnsRecordService.NormalizeZoneTxt(value));
        }

        [Fact]
        public void NormalizeZoneTxt_Enumerable_JoinsItems()
        {
            Assert.Equal("ab", DnsRecordService.NormalizeZoneTxt(new List<object> { "a", "b" }));
        }

        [Fact]
        public void ZoneRdataFromRecord_A_ReadsNestedRecordData()
        {
            var rec = TestObjects.Obj(
                ("HostName", "www"),
                ("RecordType", "A"),
                ("RecordData", TestObjects.Obj(("IPv4Address", "10.0.0.5"))));

            Assert.Equal("A 10.0.0.5", DnsRecordService.ZoneRdataFromRecord(rec, "A"));
        }

        [Fact]
        public void ZoneRdataFromRecord_UnsupportedType_ReturnsNull()
        {
            // RecordData обязателен: AsPSObject(null) в PS 5.1 бросает исключение (в реальных
            // данных этого не бывает - объект записи всегда содержит RecordData).
            var rec = TestObjects.Obj(("HostName", "@"), ("RecordType", "SOA"), ("RecordData", TestObjects.Obj()));
            Assert.Null(DnsRecordService.ZoneRdataFromRecord(rec, "SOA"));
        }

        private static PSObject ARecord(string hostName, string ip)
        {
            return TestObjects.Obj(
                ("HostName", hostName),
                ("RecordType", "A"),
                ("RecordData", TestObjects.Obj(("IPv4Address", ip))));
        }

        [Fact]
        public void FindScopeFileRecordLines_MatchesInlineAndInheritedOwner()
        {
            var lines = new List<string>
            {
                "; заголовок файла",
                "$ORIGIN corp.local.",
                "www 3600 IN A 10.0.0.5", // inline-владелец + TTL + класс
                "  A 10.0.0.5",           // владелец унаследован от www, тот же IP - тоже совпадение
                "  A 10.0.0.6",           // другой IP
                "old IN A 10.0.0.5",      // другой владелец
            };

            var hits = DnsRecordService.FindScopeFileRecordLines(lines, "corp.local", ARecord("www", "10.0.0.5"));

            Assert.Equal(new List<int> { 2, 3 }, hits); // неоднозначно - файл трогать нельзя, но индексы честные
        }

        [Fact]
        public void FindScopeFileRecordLines_SingleMatch_ReturnsSingleIndex()
        {
            var lines = new List<string> { "www IN A 10.0.0.5", "www IN A 10.0.0.6" };

            var hits = DnsRecordService.FindScopeFileRecordLines(lines, "corp.local", ARecord("www", "10.0.0.6"));

            var hit = Assert.Single(hits);
            Assert.Equal(1, hit);
        }

        [Fact]
        public void FindScopeFileRecordLines_FqdnOwnerMatchesRelative()
        {
            var lines = new List<string> { "www.corp.local. IN A 10.0.0.5" };

            var hits = DnsRecordService.FindScopeFileRecordLines(lines, "corp.local", ARecord("www", "10.0.0.5"));

            var hit = Assert.Single(hits);
            Assert.Equal(0, hit);
        }

        [Fact]
        public void FindScopeFileRecordLines_NoMatch_ReturnsEmpty()
        {
            var lines = new List<string> { "other IN A 10.0.0.5" };

            var hits = DnsRecordService.FindScopeFileRecordLines(lines, "corp.local", ARecord("www", "10.0.0.5"));

            Assert.Empty(hits);
        }

        [Fact]
        public void FindScopeFileRecordLines_UnsupportedType_ReturnsNull()
        {
            var rec = TestObjects.Obj(("HostName", "@"), ("RecordType", "SOA"), ("RecordData", TestObjects.Obj()));

            Assert.Null(DnsRecordService.FindScopeFileRecordLines(new List<string> { "@" }, "z", rec));
        }
    }
}
