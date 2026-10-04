using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RingMouse.Core.Update;
using Xunit;

namespace RingMouse.Core.Tests;

/// <summary>Update-Manifest: Versionsvergleich, Feldprüfung und RSA-Signaturprüfung.</summary>
public class UpdateManifestTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("1.1.2", "1.1.1", true)]
    [InlineData("1.2.0", "1.1.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.1.1", "1.1.1", false)]
    [InlineData("1.1.0", "1.1.1", false)]
    [InlineData("1.1.2", "1.1.2.1", false)] // 1.1.2 == 1.1.2.0 < 1.1.2.1
    [InlineData("1.1.2.1", "1.1.2", true)]
    public void IsNewer_ComparesNumerically(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdateManifest.IsNewer(candidate, current));

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("1.2", true)]
    [InlineData("1", false)]        // mindestens zwei Teile
    [InlineData("1.2.3.4.5", false)]
    [InlineData("v1.2.3", false)]
    [InlineData("1.2.x", false)]
    [InlineData("", false)]
    public void IsValidVersion_MatchesExpected(string version, bool expected) =>
        Assert.Equal(expected, UpdateManifest.IsValidVersion(version));

    [Fact]
    public void Validate_AcceptsGoodManifest() =>
        new UpdateManifest("1.1.2", "RingMouse.exe", ValidSha, 12345, "notes", "https://example.org/x.exe").Validate();

    [Theory]
    [InlineData("1.x", "RingMouse.exe", 100, null)]                 // version
    [InlineData("1.1.2", "RingMouse.bat", 100, null)]               // nicht .exe
    [InlineData("1.1.2", "../evil.exe", 100, null)]                 // Pfadtrenner im Namen
    [InlineData("1.1.2", "RingMouse.exe", 0, null)]                 // size 0
    [InlineData("1.1.2", "RingMouse.exe", 100, "http://x/x.exe")]   // url nicht https
    public void Validate_RejectsBadFields(string version, string file, long size, string? url) =>
        Assert.Throws<UpdateException>(() => new UpdateManifest(version, file, ValidSha, size, "", url).Validate());

    [Fact]
    public void Validate_RejectsBadChecksum() =>
        Assert.Throws<UpdateException>(() => new UpdateManifest("1.1.2", "RingMouse.exe", "nothex", 100, "", null).Validate());

    // --- Signatur -------------------------------------------------------------------------------------

    private static (byte[] Manifest, string PublicKey) SignedManifest(RSA key, string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var signature = ManifestVerifier.Sign(doc.RootElement, key);
        var manifest = $$"""{"signature":"{{signature}}","payload":{{payloadJson}}}""";
        return (Encoding.UTF8.GetBytes(manifest), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Verify_RoundTrip_ReturnsManifest()
    {
        using var key = RSA.Create(3072);
        var (manifest, pub) = SignedManifest(key,
            $$"""{"version":"1.2.0","file":"RingMouse.exe","sha256":"{{ValidSha}}","size":987654,"notes":"Neu"}""");

        var result = ManifestVerifier.Verify(manifest, pub);

        Assert.Equal("1.2.0", result.Version);
        Assert.Equal("RingMouse.exe", result.File);
        Assert.Equal(987654, result.Size);
        Assert.Equal("Neu", result.Notes);
    }

    [Fact]
    public void Verify_CanonicalFormIsOrderIndependent()
    {
        using var key = RSA.Create(3072);
        // Signiert mit einer Schlüsselreihenfolge ...
        using var doc = JsonDocument.Parse($$"""{"version":"1.2.0","file":"RingMouse.exe","sha256":"{{ValidSha}}","size":5,"notes":"x"}""");
        var signature = ManifestVerifier.Sign(doc.RootElement, key);
        // ... geprüft mit einer anderen Schlüsselreihenfolge im payload – die kanonische Form muss gleich sein.
        var manifest = Encoding.UTF8.GetBytes(
            $$"""{"payload":{"notes":"x","size":5,"file":"RingMouse.exe","version":"1.2.0","sha256":"{{ValidSha}}"},"signature":"{{signature}}"}""");
        var result = ManifestVerifier.Verify(manifest, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        Assert.Equal("1.2.0", result.Version);
    }

    [Fact]
    public void Verify_TamperedPayload_Throws()
    {
        using var key = RSA.Create(3072);
        var (_, pub) = SignedManifest(key,
            $$"""{"version":"1.2.0","file":"RingMouse.exe","sha256":"{{ValidSha}}","size":5,"notes":"x"}""");
        // Signatur von size:5, aber das Manifest behauptet size:999 → Signatur passt nicht mehr.
        using var doc = JsonDocument.Parse($$"""{"version":"1.2.0","file":"RingMouse.exe","sha256":"{{ValidSha}}","size":5,"notes":"x"}""");
        var signature = ManifestVerifier.Sign(doc.RootElement, key);
        var tampered = Encoding.UTF8.GetBytes(
            $$"""{"payload":{"version":"1.2.0","file":"RingMouse.exe","sha256":"{{ValidSha}}","size":999,"notes":"x"},"signature":"{{signature}}"}""");

        Assert.Throws<UpdateException>(() => ManifestVerifier.Verify(tampered, pub));
    }

    [Fact]
    public void Verify_WrongKey_Throws()
    {
        using var signer = RSA.Create(3072);
        using var other = RSA.Create(3072);
        var (manifest, _) = SignedManifest(signer,
            $$"""{"version":"1.2.0","file":"RingMouse.exe","sha256":"{{ValidSha}}","size":5,"notes":"x"}""");

        Assert.Throws<UpdateException>(() => ManifestVerifier.Verify(manifest, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void Verify_TooLarge_Throws()
    {
        var huge = new byte[ManifestVerifier.MaxManifestBytes + 1];
        Assert.Throws<UpdateException>(() => ManifestVerifier.Verify(huge, "irrelevant"));
    }

    [Fact]
    public void EmbeddedPublicKey_IsAValidRsa3072Key()
    {
        // Fängt einen beschädigten/falsch kopierten eingebauten Release-Schlüssel ab.
        Assert.False(string.IsNullOrEmpty(ManifestVerifier.PublicKey));
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ManifestVerifier.PublicKey), out _);
        Assert.Equal(3072, rsa.KeySize);
    }
}
