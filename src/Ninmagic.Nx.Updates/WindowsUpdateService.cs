using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ninmagic.Nx.Updates;

public sealed class WindowsUpdateService : IDisposable
{
    internal const long MaximumAssetBytes = 512L * 1024 * 1024;
    internal const string RepositoryApi = "https://api.github.com/repos/ANE-JV/ninmagic-nx/";
    private readonly Version currentVersion;
    private readonly HttpClient client;
    private readonly string downloadRoot;
    public WindowsUpdateService(string currentVersion) : this(currentVersion,
        new HttpClientHandler { AllowAutoRedirect = false, UseDefaultCredentials = false, Credentials = null, UseCookies = false },
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NinmagicNX", "Updates")) { }
    internal WindowsUpdateService(string version, HttpMessageHandler handler, string downloadRoot)
    {
        if (!UpdateVersions.TryParseStable(version, out currentVersion)) throw new ArgumentException("Versión actual no válida.");
        client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ninmagic-nx/" + version);
        this.downloadRoot = downloadRoot;
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        UpdateInfo? selected = null;
        for (int page = 1; page <= 5; page++)
        {
            using var response = await SendAsync(new Uri(RepositoryApi + $"releases?per_page=100&page={page}"), "application/vnd.github+json", cancellationToken);
            var bytes = await ReadBoundedAsync(response.Content, 8 * 1024 * 1024, cancellationToken);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 40 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub devolvió una lista de versiones no válida.");
            var candidate = SelectRelease(document.RootElement, currentVersion);
            if (candidate is not null && (selected is null || Version.Parse(candidate.Version) > Version.Parse(selected.Version))) selected = candidate;
            if (document.RootElement.GetArrayLength() < 100) break;
            if (page == 5) throw new IOException("El historial de versiones supera el límite de consulta. No se ha podido comprobar completamente si hay actualizaciones.");
        }
        return selected;
    }
    internal static UpdateInfo? SelectRelease(JsonElement releases, Version current)
    {
        UpdateInfo? selected = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
            if (!UpdateVersions.TryParseStable(release.GetProperty("tag_name").GetString(), out var version) || version <= current) continue;
            if (selected is not null && version <= Version.Parse(selected.Version)) continue;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != "ninmagic-nx.exe" || asset.GetProperty("state").GetString() != "uploaded") continue;
                long size = asset.GetProperty("size").GetInt64(), id = asset.GetProperty("id").GetInt64();
                if (size < 1024 || size > MaximumAssetBytes || id <= 0) continue;
                string? digest = asset.TryGetProperty("digest", out var property) ? property.GetString() : null;
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !UpdateFiles.IsSha256(digest[7..]))
                    throw new InvalidDataException("La nueva versión no incluye una huella SHA-256 válida en GitHub. No se instalará.");
                string notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
                selected = new(version.ToString(), notes.Length > 20_000 ? notes[..20_000] : notes, id, size, digest[7..].ToLowerInvariant());
                break;
            }
        }
        return selected;
    }

    public async Task<StagedUpdate> DownloadAsync(UpdateInfo update, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!UpdateVersions.TryParseStable(update.Version, out var next) || next <= currentVersion || update.AssetId <= 0 ||
            update.Size < 1024 || update.Size > MaximumAssetBytes || !UpdateFiles.IsSha256(update.Sha256)) throw new InvalidDataException("La actualización seleccionada no es válida.");
        UpdateFiles.RejectReparseAncestors(downloadRoot);
        Directory.CreateDirectory(downloadRoot);
        var stage = Path.Combine(downloadRoot, "download-" + Guid.NewGuid().ToString("N"));
        UpdateFiles.CreateSecureDirectory(stage);
        var path = Path.Combine(stage, "ninmagic-nx.exe");
        try
        {
            using var response = await SendAsync(new Uri(RepositoryApi + "releases/assets/" + update.AssetId), "application/octet-stream", cancellationToken);
            if (response.Content.Headers.ContentLength is { } announced && announced != update.Size)
                throw new InvalidDataException("El tamaño anunciado de la descarga no coincide con la versión publicada.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[128 * 1024]; long total = 0;
                progress.Report(new(0, update.Size));
                while (true)
                {
                    var count = await input.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    total = checked(total + count);
                    if (total > update.Size) throw new InvalidDataException("La descarga supera el tamaño esperado.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress.Report(new(total, update.Size));
                }
                if (total != update.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
                    throw new InvalidDataException("La descarga está incompleta o su SHA-256 no coincide. La aplicación actual se conserva.");
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            UpdateFiles.ValidateExecutable(path, update.Version);
            return new(path, update.Version, update.Sha256, update.Size);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(stage)) Directory.Delete(stage);
            throw;
        }
    }
    public void StartInstall(StagedUpdate staged) => UpdateInstaller.Start(staged, currentVersion.ToString());

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string accept, CancellationToken ct)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) throw new IOException("La descarga no usa una conexión segura admitida.");
            bool isApi = uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);
            if (isApi && !uri.AbsolutePath.StartsWith("/repos/ANE-JV/ninmagic-nx/", StringComparison.Ordinal)) throw new IOException("Repositorio de actualización no válido.");
            if (!isApi && !IsAssetHost(uri.Host)) throw new IOException("Servidor de descarga no admitido.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (isApi)
            {
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new IOException("GitHub devolvió una redirección incompleta.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            { response.Dispose(); throw new IOException("Las actualizaciones públicas no están disponibles en este momento. Inténtalo más tarde; la aplicación actual se conserva."); }
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode; response.Dispose();
                throw new IOException(status == 403 ? "GitHub ha limitado temporalmente las consultas o el servicio no está disponible. Inténtalo más tarde; la aplicación actual se conserva." : $"No se pudo consultar GitHub (HTTP {status}). La aplicación actual se conserva.");
            }
            return response;
        }
        throw new IOException("La descarga contiene demasiadas redirecciones.");
    }
    private static bool IsAssetHost(string host) => host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("La respuesta de GitHub es demasiado grande.");
        using var output = new MemoryStream(); await using var input = await content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[16 * 1024]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        { if (output.Length + count > maximum) throw new InvalidDataException("La respuesta de GitHub es demasiado grande."); await output.WriteAsync(buffer.AsMemory(0, count), ct); }
        return output.ToArray();
    }
    public void Dispose() => client.Dispose();
}
