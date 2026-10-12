using System.IO;
using DnsToolWinForms;
using Xunit;

namespace DnsToolWinForms.Tests
{
    public class UpdateApplierTests
    {
        [Fact]
        public void TryParseApplyArgs_AcceptsValidArgs()
        {
            Assert.True(UpdateApplier.TryParseApplyArgs(
                new[] { "--apply-update", @"C:\Tools\DNS Server Tool", "4242" }, out var appDir, out var pid));
            Assert.Equal(@"C:\Tools\DNS Server Tool", appDir);
            Assert.Equal(4242, pid);
        }

        [Fact]
        public void TryParseApplyArgs_PrefixCaseInsensitive()
        {
            Assert.True(UpdateApplier.TryParseApplyArgs(
                new[] { "--APPLY-UPDATE", @"C:\app", "1" }, out _, out _));
        }

        [Theory]
        [InlineData("dns")]
        [InlineData("no-flag", "C:\\app", "1")]
        [InlineData("--apply-update", "C:\\app", "не-число")]
        [InlineData("--apply-update", "", "1")]
        public void TryParseApplyArgs_RejectsBadArgs(params string[] args)
        {
            Assert.False(UpdateApplier.TryParseApplyArgs(args, out _, out _));
        }

        [Theory]
        [InlineData("settings.ini", true)]
        [InlineData("SETTINGS.INI", true)]
        [InlineData("changes.log", true)]
        [InlineData("changes.log.bak_2026-01-01", true)]
        [InlineData("update.log", true)]
        [InlineData("zone_internal.dns", true)]
        [InlineData("zone_internal.dns.bak_3", true)]
        [InlineData("banner.png", false)]
        [InlineData("DNS Server Tool.exe", false)]
        [InlineData("my.dnsbackup", false)]
        public void IsLocalDataFile_ClassifiesCorrectly(string name, bool expected)
        {
            Assert.Equal(expected, UpdateApplier.IsLocalDataFile(name));
        }

        [Fact]
        public void CopyUpdateFiles_CopiesNewAndKeepsLocalData()
        {
            var root = Path.Combine(Path.GetTempPath(), "dnstool_applier_test_" + Path.GetRandomFileName());
            var src = Path.Combine(root, "src");
            var dst = Path.Combine(root, "dst");
            try
            {
                Directory.CreateDirectory(Path.Combine(src, "sub"));
                Directory.CreateDirectory(dst);

                File.WriteAllText(Path.Combine(src, "DNS Server Tool.exe"), "NEW-EXE");
                File.WriteAllText(Path.Combine(src, "DNS Server Tool.exe.config"), "NEW-CONFIG");
                File.WriteAllText(Path.Combine(src, "banner.png"), "NEW-BANNER");
                File.WriteAllText(Path.Combine(src, "update.log"), "APPLIER-LOG");
                File.WriteAllText(Path.Combine(src, "sub", "extra.dll"), "NESTED");

                File.WriteAllText(Path.Combine(dst, "DNS Server Tool.exe"), "OLD-EXE");
                File.WriteAllText(Path.Combine(dst, "settings.ini"), "LOCAL-SETTINGS");
                File.WriteAllText(Path.Combine(dst, "changes.log"), "LOCAL-LOG");
                File.WriteAllText(Path.Combine(dst, "changes.log.bak_2026-01-01"), "LOCAL-BAK");
                File.WriteAllText(Path.Combine(dst, "zone_internal.dns"), "LOCAL-ZONE");
                File.WriteAllText(Path.Combine(dst, "zone_internal.dns.bak_1"), "LOCAL-ZONE-BAK");

                var copied = UpdateApplier.CopyUpdateFiles(src, dst);

                Assert.Equal(4, copied);
                Assert.Equal("NEW-EXE", File.ReadAllText(Path.Combine(dst, "DNS Server Tool.exe")));
                Assert.Equal("NEW-CONFIG", File.ReadAllText(Path.Combine(dst, "DNS Server Tool.exe.config")));
                Assert.Equal("NEW-BANNER", File.ReadAllText(Path.Combine(dst, "banner.png")));
                Assert.Equal("NESTED", File.ReadAllText(Path.Combine(dst, "sub", "extra.dll")));
                Assert.False(File.Exists(Path.Combine(dst, "update.log")));

                Assert.Equal("LOCAL-SETTINGS", File.ReadAllText(Path.Combine(dst, "settings.ini")));
                Assert.Equal("LOCAL-LOG", File.ReadAllText(Path.Combine(dst, "changes.log")));
                Assert.Equal("LOCAL-BAK", File.ReadAllText(Path.Combine(dst, "changes.log.bak_2026-01-01")));
                Assert.Equal("LOCAL-ZONE", File.ReadAllText(Path.Combine(dst, "zone_internal.dns")));
                Assert.Equal("LOCAL-ZONE-BAK", File.ReadAllText(Path.Combine(dst, "zone_internal.dns.bak_1")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void CopyUpdateFiles_CreatesMissingTargetDir()
        {
            var root = Path.Combine(Path.GetTempPath(), "dnstool_applier_test_" + Path.GetRandomFileName());
            var src = Path.Combine(root, "src");
            var dst = Path.Combine(root, "fresh");
            try
            {
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "banner.png"), "BANNER");
                Assert.Equal(1, UpdateApplier.CopyUpdateFiles(src, dst));
                Assert.Equal("BANNER", File.ReadAllText(Path.Combine(dst, "banner.png")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
