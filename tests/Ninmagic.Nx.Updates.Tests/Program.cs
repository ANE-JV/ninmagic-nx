using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ninmagic.Nx.Updates;

var root = Path.Combine(Path.GetTempPath(), "Ninmagic update tests " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0, failures = 0;
async Task Check(string name, Func<Task> action)
{
    checks++;
    try { await action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
}
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
var hashText = new string('a', 64);
object Release(string version, bool draft = false, bool pre = false, string asset = "ninmagic-nx.exe", string? digest = null, long size = 2000) =>
    new { tag_name = version, draft, prerelease = pre, body = "Notas de ejemplo", assets = new[] { new { name = asset, id = 12, size, state = "uploaded", digest = digest ?? "sha256:" + hashText } } };
UpdateInfo? Select(params object[] releases)
{
    using var document = JsonDocument.Parse(JsonSerializer.Serialize(releases));
    return WindowsUpdateService.SelectRelease(document.RootElement, new Version(1, 6, 0));
}
WindowsUpdateService Service(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, string version = "1.6.0", Func<string?>? token = null) =>
    new(version, new FakeHttp(handler), token ?? (() => "test-token-not-a-real-secret"), Path.Combine(root, "Downloads"));
HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
try
{
    foreach (string valid in new[] { "1.6.0", "v2.0.0", "10.20.30", "0.0.1" })
        await Check("stable version " + valid, () => { Assert(UpdateVersions.TryParseStable(valid, out _)); return Task.CompletedTask; });
    foreach (string invalid in new[] { "1.6", "1.6.0.0", "v1.6.0-rc.1", "macos-v2.0.0", "1.6.0+abc", "01.6.0", "1.-6.0", "v1.6.0 " })
        await Check("reject version " + invalid, () => { Assert(!UpdateVersions.TryParseStable(invalid, out _)); return Task.CompletedTask; });
    await Check("select newest stable Windows exe independently of order", () => { Assert(Select(Release("v1.7.0"), Release("v3.0.0"), Release("v2.0.0"))?.Version == "3.0.0"); return Task.CompletedTask; });
    await Check("old and current versions excluded", () => { Assert(Select(Release("v1.5.2"), Release("v1.6.0")) is null); return Task.CompletedTask; });
    await Check("draft and prerelease excluded", () => { Assert(Select(Release("v2.0.0", draft: true), Release("v3.0.0", pre: true), Release("v4.0.0-rc.1")) is null); return Task.CompletedTask; });
    await Check("Mac and source archives excluded", () => { Assert(Select(Release("macos-v2.0.0"), Release("v2.0.0", asset: "fuentes.zip"), Release("v3.0.0", asset: "ninmagic-nx.app")) is null); return Task.CompletedTask; });
    await Check("missing trusted digest fails closed", () => Throws<InvalidDataException>(() => { Select(Release("v2.0.0", digest: "")); return Task.CompletedTask; }));
    await Check("oversized assets excluded", () => { Assert(Select(Release("v2.0.0", size: WindowsUpdateService.MaximumAssetBytes + 1)) is null); return Task.CompletedTask; });
    await Check("no new version is distinct from lack of access", async () => { using var service = Service((_, _) => Task.FromResult(Json("[]"))); Assert(await service.CheckAsync(default) is null); });
    foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound })
        await Check("private auth status " + status, async () => { using var service = Service((_, _) => Task.FromResult(new HttpResponseMessage(status))); await Throws<UnauthorizedAccessException>(() => service.CheckAsync(default)); });
    await Check("rate limit never masquerades as up to date", async () => { using var service = Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))); await Throws<IOException>(() => service.CheckAsync(default)); });
    await Check("bounded release response", async () => { using var service = Service((_, _) => Task.FromResult(Json(new string(' ', 8 * 1024 * 1024 + 1)))); await Throws<InvalidDataException>(() => service.CheckAsync(default)); });
    await Check("pagination finds newer release on next page", async () =>
    {
        int page = 0;
        using var service = Service((_, _) => Task.FromResult(Json(++page == 1 ? JsonSerializer.Serialize(Enumerable.Repeat(Release("v1.0.0"), 100)) : JsonSerializer.Serialize(new[] { Release("v2.0.0") }))));
        Assert((await service.CheckAsync(default))?.Version == "2.0.0" && page == 2);
    });
    await Check("pagination limit reports incomplete check", async () =>
    {
        using var service = Service((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(Enumerable.Repeat(Release("v1.0.0"), 100)))));
        await Throws<IOException>(() => service.CheckAsync(default));
    });
    await Check("cancellation before request", async () =>
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var service = Service((_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(Json("[]")); });
        await Throws<OperationCanceledException>(() => service.CheckAsync(cancelled.Token));
    });
    var fixturePath = Environment.ProcessPath!;
    var fixture = await File.ReadAllBytesAsync(fixturePath);
    var fixtureHash = Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant();
    var update = new UpdateInfo("9.0.0", "Test", 12, fixture.Length, fixtureHash);
    await Check("fixture is a Windows x64 executable with expected product and version", () => { UpdateFiles.ValidateExecutable(fixturePath, "9.0.0"); return Task.CompletedTask; });
    await Check("valid download checks size hash PE architecture and version", async () =>
    {
        using var service = Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture) }));
        var progress = new List<UpdateProgress>();
        var staged = await service.DownloadAsync(update, new DirectProgress(progress.Add), default);
        Assert(File.Exists(staged.FilePath) && UpdateFiles.Hash(staged.FilePath) == fixtureHash && progress[^1].Completed == fixture.Length);
        UpdateFiles.ValidateSecureDirectory(Path.GetDirectoryName(staged.FilePath)!);
    });
    await Check("token goes only to GitHub API, never asset CDN", async () =>
    {
        int requestCount = 0;
        using var service = Service((request, _) =>
        {
            if (++requestCount == 1)
            {
                Assert(request.RequestUri!.Host == "api.github.com" && request.Headers.Authorization?.Parameter == "test-token-not-a-real-secret");
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/example?download=1"); return Task.FromResult(redirect);
            }
            Assert(request.Headers.Authorization is null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture) });
        });
        await service.DownloadAsync(update, new DirectProgress(_ => { }), default); Assert(requestCount == 2);
    });
    foreach (string location in new[] { "http://release-assets.githubusercontent.com/file", "https://evil.example/file", "https://api.github.com:444/repos/ANE-JV/ninmagic-nx/releases/assets/1", "https://api.github.com/repos/other/repo/releases/assets/1", "https://someone@api.github.com/repos/ANE-JV/ninmagic-nx/releases/assets/1" })
        await Check("reject redirect " + new Uri(location).Host + new Uri(location).Port, async () =>
        {
            using var service = Service((_, _) => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri(location); return Task.FromResult(response); });
            await Throws<IOException>(() => service.DownloadAsync(update, new DirectProgress(_ => { }), default));
        });
    await Check("hash mismatch leaves no partial download", async () =>
    {
        using var service = Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture) }));
        var before = Directory.GetFiles(root, "ninmagic-nx.exe", SearchOption.AllDirectories).Length;
        await Throws<InvalidDataException>(() => service.DownloadAsync(update with { Sha256 = hashText }, new DirectProgress(_ => { }), default));
        Assert(before == Directory.GetFiles(root, "ninmagic-nx.exe", SearchOption.AllDirectories).Length);
    });
    foreach (int delta in new[] { -1, 1 })
        await Check("short or extra bytes rejected " + delta, async () =>
        {
            using var service = Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture) }));
            await Throws<InvalidDataException>(() => service.DownloadAsync(update with { Size = fixture.Length + delta }, new DirectProgress(_ => { }), default));
        });
    await Check("cancel mid-download removes partial data", async () =>
    {
        using var cancellation = new CancellationTokenSource();
        using var service = Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture) }));
        var before = Directory.GetFiles(root, "ninmagic-nx.exe", SearchOption.AllDirectories).Length;
        await Throws<OperationCanceledException>(() => service.DownloadAsync(update, new DirectProgress(value => { if (value.Completed > 0) cancellation.Cancel(); }), cancellation.Token));
        Assert(before == Directory.GetFiles(root, "ninmagic-nx.exe", SearchOption.AllDirectories).Length);
    });
    await Check("valid SHA does not bypass executable version validation", async () =>
    {
        using var service = Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture) }));
        await Throws<InvalidDataException>(() => service.DownloadAsync(update with { Version = "10.0.0" }, new DirectProgress(_ => { }), default));
    });
    await Check("UNC staging path rejected", () => Throws<IOException>(() => { UpdateFiles.RejectReparseAncestors("\\\\server\\share\\file.exe"); return Task.CompletedTask; }));
    await Check("plain directory cannot impersonate protected stage", () => Throws<IOException>(() => { UpdateFiles.ValidateSecureDirectory(root); return Task.CompletedTask; }));
    await Check("single instance lock blocks a second thread", async () =>
    {
        // A mutex is thread-affine. Keep acquisition/disposal on this thread without await.
        bool blocked = false;
        using (UpdateInstanceGuard.TryAcquire(fixturePath) ?? throw new InvalidOperationException("Missing first lease"))
        {
            var other = new Thread(() => { using var second = UpdateInstanceGuard.TryAcquire(fixturePath); blocked = second is null; });
            other.Start(); other.Join();
        }
        Assert(blocked);
        await Task.CompletedTask;
    });
    await Check("invalid installer args never touch files", async () => { var result = await UpdateInstaller.RunAsync(new[] { "--bad" }); Assert(!result.Success); });
    await Check("partially completed replacement restores exact old bytes", async () =>
    {
        var target = Path.Combine(root, "recover-target.exe"); var backup = Path.Combine(root, "recover-backup.exe");
        await File.WriteAllBytesAsync(backup, fixture);
        UpdateInstaller.RecoverInterruptedReplacement(target, backup, fixtureHash);
        Assert(UpdateFiles.Hash(target) == fixtureHash && !File.Exists(backup));
    });
    await Check("recovery never overwrites an existing executable", async () =>
    {
        var target = Path.Combine(root, "existing-target.exe"); var backup = Path.Combine(root, "unused-backup.exe");
        await File.WriteAllTextAsync(target, "Existing application"); await File.WriteAllBytesAsync(backup, fixture);
        UpdateInstaller.RecoverInterruptedReplacement(target, backup, fixtureHash);
        Assert(await File.ReadAllTextAsync(target) == "Existing application" && File.Exists(backup));
    });
    await Check("partial replacement recovery rejects a corrupted backup", async () =>
    {
        var target = Path.Combine(root, "absent-target.exe"); var backup = Path.Combine(root, "bad-backup.exe");
        await File.WriteAllTextAsync(backup, "Corrupted backup");
        await Throws<InvalidDataException>(() => { UpdateInstaller.RecoverInterruptedReplacement(target, backup, fixtureHash); return Task.CompletedTask; });
        Assert(!File.Exists(target) && File.Exists(backup));
    });
}
finally
{
    // The only deleted tree is the unique disposable directory created by this test suite.
    var full = Path.GetFullPath(root);
    if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("Ninmagic update tests ", StringComparison.Ordinal)) Directory.Delete(full, recursive: true);
}
Console.WriteLine($"RESULT: {checks} update checks; {failures} failures. No cards or existing applications were modified.");
return failures == 0 ? 0 : 1;

sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
sealed class DirectProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
{
    public void Report(UpdateProgress value) => report(value);
}
