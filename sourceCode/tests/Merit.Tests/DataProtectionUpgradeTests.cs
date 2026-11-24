using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.Tests;

/// <summary>
/// Verifies that upgrading an existing unencrypted Data Protection key ring to certificate-protected keys (#301)
/// continues to decrypt enrolled MFA secrets while encrypting newly generated keys at rest.
/// </summary>
public sealed class DataProtectionUpgradeTests
{
    [Fact]
    public void UpgradingPlaintextKeyRingToCertificatePreservesExistingMfaSecretsAndEncryptsNewKeys()
    {
        string keyDir = Path.Combine(Path.GetTempPath(), "merit-dp-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyDir);
        try
        {
            // Step 1: Legacy deployment writes an unencrypted key and protects an enrolled MFA TOTP secret.
            string protectedMfaSecret;
            using (var legacyProvider = new ServiceCollection()
                .AddDataProtection()
                .SetApplicationName("MERIT")
                .PersistKeysToFileSystem(new DirectoryInfo(keyDir))
                .Services.BuildServiceProvider())
            {
                var protector = new DataProtectionSecretProtector(legacyProvider.GetRequiredService<IDataProtectionProvider>());
                protectedMfaSecret = protector.Protect("JBSWY3DPEHPK3PXP");
            }

            string[] legacyFiles = Directory.GetFiles(keyDir, "key-*.xml");
            Assert.NotEmpty(legacyFiles);

            // Step 2: Upgraded deployment configures ProtectKeysWithCertificate on the same key ring.
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=MERIT-Test-DP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            byte[] pfxBytes = ephemeral.Export(X509ContentType.Pfx, "test-pass");
            using var cert = X509CertificateLoader.LoadPkcs12(pfxBytes, "test-pass", X509KeyStorageFlags.Exportable);

            using (var upgradedProvider = new ServiceCollection()
                .AddDataProtection()
                .SetApplicationName("MERIT")
                .PersistKeysToFileSystem(new DirectoryInfo(keyDir))
                .ProtectKeysWithCertificate(cert)
                .Services.BuildServiceProvider())
            {
                // Existing enrolled MFA secret still decrypts cleanly.
                var protector = new DataProtectionSecretProtector(upgradedProvider.GetRequiredService<IDataProtectionProvider>());
                Assert.Equal("JBSWY3DPEHPK3PXP", protector.Unprotect(protectedMfaSecret));

                // Newly generated keys in the upgraded ring are encrypted at rest.
                var keyManager = upgradedProvider.GetRequiredService<IKeyManager>();
                keyManager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));

                string[] allFiles = Directory.GetFiles(keyDir, "key-*.xml");
                string newFile = Assert.Single(allFiles.Except(legacyFiles));
                string newXml = File.ReadAllText(newFile);
                Assert.Contains("encryptedSecret", newXml, StringComparison.OrdinalIgnoreCase);

                // Newly protected secrets also round-trip cleanly.
                string newProtected = protector.Protect("KRSXG5CTMVRXEZLU");
                Assert.Equal("KRSXG5CTMVRXEZLU", protector.Unprotect(newProtected));
            }
        }
        finally
        {
            if (Directory.Exists(keyDir))
            {
                Directory.Delete(keyDir, recursive: true);
            }
        }
    }
}
