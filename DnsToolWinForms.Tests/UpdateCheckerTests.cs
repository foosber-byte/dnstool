using System.Security.Cryptography;
using DnsToolWinForms;
using Xunit;

namespace DnsToolWinForms.Tests
{
    public class UpdateCheckerTests
    {
        private static byte[] SampleData()
        {
            var data = new byte[512];
            for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 7);
            return data;
        }

        [Fact]
        public void VerifySignature_AcceptsValidSignature()
        {
            var data = SampleData();
            using (var rsa = RSA.Create(2048))
            {
                var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                Assert.True(UpdateChecker.VerifySignature(data, sig, rsa));
            }
        }

        [Fact]
        public void VerifySignature_RejectsTamperedArchive()
        {
            var data = SampleData();
            using (var rsa = RSA.Create(2048))
            {
                var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                data[10] ^= 0xFF;
                Assert.False(UpdateChecker.VerifySignature(data, sig, rsa));
            }
        }

        [Fact]
        public void VerifySignature_RejectsSignatureOfOtherKey()
        {
            var data = SampleData();
            using (var signer = RSA.Create(2048))
            {
                var sig = signer.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using (var other = RSA.Create(2048))
                    Assert.False(UpdateChecker.VerifySignature(data, sig, other));
            }
        }

        [Fact]
        public void VerifySignature_RejectsGarbageAndNulls()
        {
            var data = SampleData();
            using (var rsa = RSA.Create(2048))
            {
                Assert.False(UpdateChecker.VerifySignature(data, new byte[] { 1, 2, 3 }, rsa));
                Assert.False(UpdateChecker.VerifySignature(data, null, rsa));
                Assert.False(UpdateChecker.VerifySignature(null, new byte[] { 1 }, rsa));
                Assert.False(UpdateChecker.VerifySignature(data, new byte[] { 1 }, null));
            }
        }

        [Theory]
        [InlineData("2.9.1", "2.8.1", true)]
        [InlineData("2.9.1", "2.9.1", false)]
        [InlineData("2.8.0", "2.8.1", false)]
        [InlineData("10.0.0", "9.99.99", true)]
        [InlineData("мусор", "2.8.1", false)]
        [InlineData("2.9.1", "мусор", false)]
        public void IsNewer_ComparesVersionsCorrectly(string latest, string current, bool expected)
        {
            Assert.Equal(expected, UpdateChecker.IsNewer(latest, current));
        }
    }
}
