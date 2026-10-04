using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RingMouse.Core.Update;

/// <summary>
/// Prüft die Signatur eines <c>latest.json</c> und liefert den signierten Inhalt. Nur ein mit dem privaten
/// Release-Schlüssel signiertes Manifest wird akzeptiert – wer die Download-Quelle oder das GitHub-Konto
/// übernimmt, kann deshalb trotzdem keinen fremden Code verteilen. Signatur: RSA (PSS, SHA-256) über die
/// kanonische Form des "payload" (sortierte Schlüssel, kein Leerraum) mit festem Kontext-Präfix.
/// </summary>
public static class ManifestVerifier
{
    /// <summary>
    /// Öffentlicher Teil des Release-Schlüssels (RSA, SubjectPublicKeyInfo, Base64). Der private Teil liegt allein
    /// beim Herausgeber; im Programm steckt nur dieser öffentliche Teil. Erzeugt mit dem Schlüssel-Tool.
    /// </summary>
    public const string PublicKey =
        "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA1+jy4MGAeoHd+1DEw7Lg6kaZkPa+eHAXCcxdwGUXCItVRHsKM0qXbFNfWSv7CZOQRXf" +
        "XMsAYX/54sLVmXh5CIyXuL6SpX0pXNFs/GIYIiEK9ZyK65EimNW8gNPkJ34v+gR8MSJrKX/bKgLA3JYTsK7+JY/HRkaXxaSjQAKesYtrh2Cvvj6" +
        "4++sMPuK7TBB9EAAHX3Wd6/OvRJeQ8Sv20pKIw8bx04rfv6Lit60Pp4kVj9XRUzOX2zBQsGBD0n/O/i1PpqEqcptxBtgI/RCajl1HqBg9Q6tEEd" +
        "3TOOb1Mqf5ax3FTt98JJBgatp3zBjVyFFo/QNSfD3WqPiC59Ggd/EwqL29UEUSfQZDddQI6Gjsk7aNSEaVs/Ha2p9dZC3skgPz+MPsBWGKeuIaG" +
        "7m8GuqLsHQRYKEU+plqmphWdMsyzfruT/Rd3PVYkjc5u9mqbxAKipvbk9Oy8eWFRpRtUiutJak8Y2aCw8PkSINQ0yY99gXNDuu/I01JJ/KSGBuW" +
        "xAgMBAAE=";

    /// <summary>Eine Signatur gilt nur für diesen Zweck. Unveränderlich – sonst lehnen installierte Versionen jedes Update ab.</summary>
    private static readonly byte[] SignatureContext = "RingMouse-update-v1\n"u8.ToArray();

    public const int MaxManifestBytes = 64 * 1024;

    /// <summary>Prüft Größe, Signatur und Felder eines latest.json. Wirft <see cref="UpdateException"/>.</summary>
    public static UpdateManifest Verify(byte[] rawManifest, string? publicKeyBase64 = null)
    {
        if (rawManifest.Length > MaxManifestBytes) throw new UpdateException("Manifest too large");

        var key = publicKeyBase64 ?? PublicKey;
        if (string.IsNullOrEmpty(key)) throw new UpdateException("No update public key is configured");

        try
        {
            using var doc = JsonDocument.Parse(rawManifest);
            var root = doc.RootElement;
            var payload = root.GetProperty("payload");
            if (payload.ValueKind != JsonValueKind.Object) throw new UpdateException("Manifest unreadable: payload missing");
            var signature = Convert.FromBase64String(root.GetProperty("signature").GetString()
                                                      ?? throw new UpdateException("Manifest unreadable: signature missing"));

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            if (!rsa.VerifyData(CanonicalBytes(payload), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new UpdateException("Signature invalid – this update will not be installed");

            var manifest = ParsePayload(payload);
            manifest.Validate();
            return manifest;
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or CryptographicException)
        {
            throw new UpdateException($"Manifest unreadable or key invalid: {e.Message}", e);
        }
    }

    /// <summary>Signiert ein Payload mit dem privaten Schlüssel (für das Release-Tool und Tests). Liefert die Base64-Signatur.</summary>
    public static string Sign(JsonElement payload, RSA privateKey) =>
        Convert.ToBase64String(privateKey.SignData(CanonicalBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

    /// <summary>Kanonische Bytes, über die signiert wird: Kontext-Präfix + payload mit sortierten Schlüsseln, ohne Leerraum.</summary>
    public static byte[] CanonicalBytes(JsonElement payload)
    {
        using var stream = new MemoryStream();
        stream.Write(SignatureContext);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteCanonical(writer, payload);
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer); // Zeichenkette/Zahl/Bool/Null unverändert übernehmen
                break;
        }
    }

    private static UpdateManifest ParsePayload(JsonElement p)
    {
        string Str(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string? OptStr(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long Num(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : -1;
        return new UpdateManifest(Str("version"), Str("file"), Str("sha256"), Num("size"), OptStr("notes") ?? "", OptStr("url"));
    }
}
