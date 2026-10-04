using System.Net.Http;
using System.Security.Cryptography;
using RingMouse.Core.Update;

namespace RingMouse.Platform.Update;

/// <summary>Holt das Manifest und lädt die Setup-Exe – nur über HTTPS, mit Größenlimit und SHA-256-Prüfung.</summary>
public sealed class UpdateHttp : IUpdateTransport, IDisposable
{
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;

    public UpdateHttp(string? userAgent = null)
    {
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = NetworkTimeout,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? "RingMouse-Updater");
    }

    /// <summary>Lädt bis zu <paramref name="limit"/> Bytes (für latest.json). Wirft <see cref="UpdateException"/>.</summary>
    public async Task<byte[]> FetchManifestAsync(string url, int limit, CancellationToken ct = default)
    {
        RequireHttps(url);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"Update source returned HTTP {(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit) throw new UpdateException("Response from the update source is too large");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new UpdateException($"Update source not reachable: {e.Message}", e);
        }
    }

    /// <summary>Lädt die Setup-Exe nach <paramref name="dest"/> (über .part) und prüft Größe und SHA-256.</summary>
    public async Task DownloadAsync(string url, string dest, string sha256, long size,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        RequireHttps(url);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        try
        {
            using (var sha = SHA256.Create())
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new UpdateException($"Update source returned HTTP {(int)response.StatusCode}");
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var chunk = new byte[1 << 20];
                long received = 0;
                int read;
                while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > size) throw new UpdateException("Setup is larger than announced");
                    sha.TransformBlock(chunk, 0, read, null, 0);
                    await file.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (progress is not null && size > 0) progress(received / (double)size);
                }
                sha.TransformFinalBlock([], 0, 0);
                var hash = Convert.ToHexStringLower(sha.Hash!);
                if (received != size || hash != sha256)
                    throw new UpdateException("Checksum of the setup does not match – download discarded");
            }
            File.Move(part, dest, overwrite: true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new UpdateException($"Download failed: {e.Message}", e);
        }
        finally
        {
            try { File.Delete(part); } catch { /* egal */ }
        }
    }

    /// <summary>true, wenn die Datei existiert und Größe und SHA-256 stimmen (bereits geladenes Setup).</summary>
    public bool FileMatches(string path, string sha256, long size)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != size) return false;
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == sha256;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException($"Only https addresses are allowed: {url}");
    }

    public void Dispose() => _http.Dispose();
}
