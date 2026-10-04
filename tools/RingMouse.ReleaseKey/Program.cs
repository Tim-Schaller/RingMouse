using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RingMouse.Core.Update;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0) return Usage();
    try
    {
        return args[0].ToLowerInvariant() switch
        {
            "keygen" => KeyGen(args),
            "manifest" => Manifest(args),
            "verify" => Verify(args),
            _ => Usage(),
        };
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Error: " + ex.Message);
        return 2;
    }
}

static int Usage()
{
    Console.WriteLine(
        """
        RingMouse release signing tool

          keygen [--out <private-key-file>]
              Create an RSA-3072 signing key pair. Writes the PRIVATE key (keep it secret, never
              commit it, back it up) and prints the PUBLIC key (Base64) to paste into
              ManifestVerifier.PublicKey.

          manifest --key <private-key-file> --version <X.Y.Z> --setup <RingMouse.exe>
                   [--file <name>] [--notes-file <file>] [--url <https-url>] [--out <latest.json>]
              Build and sign latest.json for a release (then verify it like a client would).

          verify --manifest <latest.json> --pubkey <base64>
              Verify a signed latest.json.
        """);
    return 1;
}

static int KeyGen(string[] args)
{
    var outFile = Arg(args, "--out") ?? "ringmouse-release.key";
    using var rsa = RSA.Create(3072);
    File.WriteAllText(outFile, Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()));
    Console.WriteLine($"Private key written to: {Path.GetFullPath(outFile)}");
    Console.WriteLine("  KEEP THIS SECRET - do not commit it, and back it up. Anyone with it can sign updates.");
    Console.WriteLine();
    Console.WriteLine("Public key (paste into ManifestVerifier.PublicKey):");
    Console.WriteLine(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
    return 0;
}

static int Manifest(string[] args)
{
    var keyFile = Require(args, "--key");
    var version = Require(args, "--version");
    var setup = Require(args, "--setup");
    var file = Arg(args, "--file") ?? Path.GetFileName(setup);
    var outFile = Arg(args, "--out") ?? "latest.json";

    if (!UpdateManifest.IsValidVersion(version)) throw new Exception($"Invalid version: {version}");
    if (!File.Exists(setup)) throw new Exception($"Setup not found: {setup}");

    var size = new FileInfo(setup).Length;
    string sha;
    using (var stream = File.OpenRead(setup)) sha = Convert.ToHexStringLower(SHA256.HashData(stream));
    var notesFile = Arg(args, "--notes-file");
    var notes = notesFile is not null ? File.ReadAllText(notesFile).Trim() : "";

    var payload = new JsonObject
    {
        ["version"] = version,
        ["file"] = file,
        ["sha256"] = sha,
        ["size"] = size,
        ["notes"] = notes,
    };
    if (Arg(args, "--url") is { } url) payload["url"] = url;

    using var rsa = RSA.Create();
    rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyFile).Trim()), out _);

    using (var doc = JsonDocument.Parse(payload.ToJsonString()))
    {
        var signature = ManifestVerifier.Sign(doc.RootElement, rsa);
        var manifest = new JsonObject { ["payload"] = JsonNode.Parse(payload.ToJsonString()), ["signature"] = signature };
        File.WriteAllText(outFile, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // Selbsttest: wie ein Client prüfen
    var verified = ManifestVerifier.Verify(File.ReadAllBytes(outFile), Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
    Console.WriteLine($"Wrote and verified {Path.GetFullPath(outFile)}: version {verified.Version}, {size} bytes, sha256 {sha}");
    return 0;
}

static int Verify(string[] args)
{
    var m = ManifestVerifier.Verify(File.ReadAllBytes(Require(args, "--manifest")), Require(args, "--pubkey"));
    Console.WriteLine($"Valid: version {m.Version}, file {m.File}, {m.Size} bytes, sha256 {m.Sha256}");
    return 0;
}

static string? Arg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string Require(string[] args, string name) => Arg(args, name) ?? throw new Exception($"Missing argument: {name}");
