using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Ninmagic.Nx.Updates;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The updater integration test requires Windows.");
var root = FindRepository();
var dotnet = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
var testRoot = Path.Combine(Path.GetTempPath(), "NinmagicNxUpdateIntegration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int checks = 0;
bool completed = false;
try
{
    var oldExe = await Publish("1.0.0", "old");
    var newExe = await Publish("2.0.0", "new");
    await SuccessfulUpdate(oldExe, newExe);
    await TamperedDownload(oldExe, newExe);
    await WrongParentIdentity(oldExe, newExe);
    LockedExecutable(oldExe, newExe);
    RelaunchRollback(oldExe, newExe);
    completed = true;
    Console.WriteLine($"RESULT: {checks} updater integration checks; real disposable processes only; no UI or disk access.");
    return 0;
}
finally
{
    // This exact unique directory was created above; never use caller-provided cleanup paths.
    if (completed && Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
    else Console.Error.WriteLine("Integration diagnostic files: " + testRoot);
}

async Task<string> Publish(string version, string label)
{
    string output = Path.Combine(testRoot, "build-" + label);
    var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true,
        RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
    foreach (var argument in new[] { "publish", "tools/UpdateProbe/UpdateProbe.csproj", "-c", "Release", "-r", "win-x64",
        "--self-contained", "true", "-p:Version=" + version, "-p:FileVersion=" + version + ".0",
        "--artifacts-path", Path.Combine(testRoot, "artifacts-" + label), "-o", output, "--nologo" })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Could not start disposable probe compilation.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    var outputText = await stdout;
    var errorText = await stderr;
    if (process.ExitCode != 0) throw new IOException("Probe compilation failed:\n" + outputText + errorText);
    return Path.Combine(output, "ninmagic-nx.exe");
}

async Task SuccessfulUpdate(string oldExe, string newExe)
{
    string directory = Path.Combine(testRoot, "success with spaces");
    Directory.CreateDirectory(directory);
    string target = Path.Combine(directory, "ninmagic-nx.exe");
    File.Copy(oldExe, target);
    var untouched = Path.Combine(directory, "copias");
    Directory.CreateDirectory(untouched);
    await File.WriteAllTextAsync(Path.Combine(untouched, "manifest.json"), "synthetic backup sentinel");
    await File.WriteAllTextAsync(Path.Combine(directory, "preferences.json"), "synthetic preferences sentinel");
    var originalHash = Hash(target);
    var expectedHash = Hash(newExe);
    using var parent = Start(target, directory, "--probe-install", newExe, "2.0.0", expectedHash);
    try
    {
        await WaitForFile(Path.Combine(directory, "started-1.0.0.txt"), parent);
        await WaitForFile(Path.Combine(directory, "prepared.txt"), parent);
        var stage = Directory.EnumerateDirectories(directory, ".ninmagic-update-*").Single();
        await WaitForFile(Path.Combine(directory, "helper-pid.txt"), parent);
        using var helper = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "helper-pid.txt"))));
        Check("production StartInstall obtains helper readiness", File.Exists(Path.Combine(stage, "ready.txt")));
        await Task.Delay(200);
        Check("helper waits without killing the application", !parent.HasExited);
        Check("original is unchanged while parent is running", Hash(target) == originalHash);
        await File.WriteAllTextAsync(Path.Combine(directory, "stop-1.0.0.txt"), "stop");
        await WaitForExit(parent);
        await WaitForExit(helper);
        Check("helper finishes successfully: " + await File.ReadAllTextAsync(Path.Combine(directory, "helper-result.txt")), helper.ExitCode == 0);
        await WaitForFile(Path.Combine(directory, "started-2.0.0.txt"));
        var restartedPid = int.Parse((await File.ReadAllLinesAsync(Path.Combine(directory, "started-2.0.0.txt")))[0]);
        try { using var restarted = Process.GetProcessById(restartedPid); await WaitForExit(restarted); }
        catch (ArgumentException) { /* The short-lived probe has already exited. */ }
        Check("target is the downloaded executable", Hash(target) == expectedHash);
        Check("new executable is relaunched", File.Exists(Path.Combine(directory, "started-2.0.0.txt")));
        Check("backup and preferences beside the executable are preserved", await File.ReadAllTextAsync(Path.Combine(untouched, "manifest.json")) == "synthetic backup sentinel" &&
            await File.ReadAllTextAsync(Path.Combine(directory, "preferences.json")) == "synthetic preferences sentinel");
        Check("old executable remains recoverable", Directory.EnumerateFiles(directory, "ninmagic-nx.exe.previous-*.exe")
            .Any(p => Hash(p) == originalHash));
    }
    finally { await StopProbe(parent, directory); }
}

void RelaunchRollback(string oldExe, string newExe)
{
    string directory = Path.Combine(testRoot, "launch failure rollback");
    Directory.CreateDirectory(directory);
    string target = Path.Combine(directory, "ninmagic-nx.exe");
    File.Copy(oldExe, target);
    using var parent = Process.GetCurrentProcess();
    string stage = PrepareStage(directory, target, newExe, parent);
    var request = JsonSerializer.Deserialize<InstallRequest>(File.ReadAllText(Path.Combine(stage, "install.json")))!;
    bool rejected = false;
    try { UpdateInstaller.ApplyTransaction(request, _ => throw new IOException("Injected launch failure")); }
    catch (IOException error) { rejected = error.Message.Contains("restaurado el ejecutable anterior", StringComparison.Ordinal); }
    Check("launch failure reports recovery", rejected);
    Check("launch failure restores exact previous executable", Hash(target) == Hash(oldExe));
    Check("failed replacement is retained separately", Directory.EnumerateFiles(directory, "ninmagic-nx.exe.failed-*.exe").Any(path => Hash(path) == Hash(newExe)));
}

void LockedExecutable(string oldExe, string newExe)
{
    string directory = Path.Combine(testRoot, "locked target");
    Directory.CreateDirectory(directory);
    string target = Path.Combine(directory, "ninmagic-nx.exe");
    File.Copy(oldExe, target);
    using var parent = Process.GetCurrentProcess();
    string stage = PrepareStage(directory, target, newExe, parent);
    var request = JsonSerializer.Deserialize<InstallRequest>(File.ReadAllText(Path.Combine(stage, "install.json")))!;
    bool rejected = false, launched = false;
    using (var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { UpdateInstaller.ApplyTransaction(request, _ => launched = true); }
        catch (IOException) { rejected = true; }
    }
    Check("locked executable prevents replacement and launch", rejected && !launched);
    Check("locked executable remains unchanged", Hash(target) == Hash(oldExe));
}

async Task WrongParentIdentity(string oldExe, string newExe)
{
    string directory = Path.Combine(testRoot, "wrong parent identity");
    Directory.CreateDirectory(directory);
    string target = Path.Combine(directory, "ninmagic-nx.exe");
    File.Copy(oldExe, target);
    using var parent = Start(target, directory, "--probe-wait");
    try
    {
        await WaitForFile(Path.Combine(directory, "started-1.0.0.txt"), parent);
        var stage = PrepareStage(directory, target, newExe, parent);
        string manifest = Path.Combine(stage, "install.json");
        var request = JsonSerializer.Deserialize<InstallRequest>(File.ReadAllText(manifest))!;
        File.WriteAllText(manifest, JsonSerializer.Serialize(request with { ParentStartTimeUtcTicks = request.ParentStartTimeUtcTicks - 1 }));
        using var helper = Start(Path.Combine(stage, "updater.exe"), directory, "--apply-update", manifest);
        await WaitForExit(helper);
        Check("same PID with wrong start time is rejected", helper.ExitCode != 0);
        Check("rejected parent identity does not stop the running application", !parent.HasExited);
        Check("rejected parent identity preserves the current executable", Hash(target) == Hash(oldExe));
    }
    finally { await StopProbe(parent, directory); }
}

async Task TamperedDownload(string oldExe, string newExe)
{
    string directory = Path.Combine(testRoot, "tampered");
    Directory.CreateDirectory(directory);
    string target = Path.Combine(directory, "ninmagic-nx.exe");
    File.Copy(oldExe, target);
    var originalHash = Hash(target);
    using var parent = Start(target, directory, "--probe-wait");
    try
    {
        await WaitForFile(Path.Combine(directory, "started-1.0.0.txt"), parent);
        var stage = PrepareStage(directory, target, newExe, parent);
        using (var tamper = new FileStream(Path.Combine(stage, "new.exe"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { tamper.Position = tamper.Length - 1; var value = tamper.ReadByte(); tamper.Position--; tamper.WriteByte((byte)(value ^ 0xFF)); }
        using var helper = Start(Path.Combine(stage, "updater.exe"), directory, "--apply-update", Path.Combine(stage, "install.json"));
        await File.WriteAllTextAsync(Path.Combine(directory, "stop-1.0.0.txt"), "stop");
        await WaitForExit(parent);
        await WaitForExit(helper);
        Check("tampered download is rejected", helper.ExitCode != 0);
        Check("tamper leaves original executable unchanged", Hash(target) == originalHash);
        Check("tampered executable is not launched", !File.Exists(Path.Combine(directory, "started-2.0.0.txt")));
    }
    finally { await StopProbe(parent, directory); }
}

string PrepareStage(string directory, string target, string download, Process parent)
{
    string stage = Path.Combine(directory, ".ninmagic-update-" + Guid.NewGuid().ToString("N"));
    UpdateFiles.CreateSecureDirectory(stage);
    var newPath = Path.Combine(stage, "new.exe");
    File.Copy(download, newPath);
    File.Copy(target, Path.Combine(stage, "updater.exe"));
    var request = new { TargetExe = target, NewExe = newPath, OriginalSha256 = Hash(target), NewSha256 = Hash(download),
        CurrentVersion = "1.0.0", NewVersion = "2.0.0", ParentId = parent.Id,
        ParentStartTimeUtcTicks = parent.StartTime.ToUniversalTime().Ticks };
    File.WriteAllText(Path.Combine(stage, "install.json"), JsonSerializer.Serialize(request));
    return stage;
}

Process Start(string executable, string outputDirectory, params string[] arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = outputDirectory };
    start.Environment["NINMAGIC_UPDATE_PROBE_DIRECTORY"] = outputDirectory;
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new IOException("Could not start disposable updater probe.");
}

async Task WaitForFile(string path, Process? producer = null)
{
    var timeout = Stopwatch.StartNew();
    while (!File.Exists(path))
    {
        if (producer is { HasExited: true }) throw new IOException($"Probe exited ({producer.ExitCode}) before creating {Path.GetFileName(path)}.");
        if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Timed out waiting for " + Path.GetFileName(path));
        await Task.Delay(50);
    }
}

async Task WaitForExit(Process process)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await process.WaitForExitAsync(timeout.Token);
}

async Task StopProbe(Process process, string directory)
{
    if (process.HasExited) return;
    await File.WriteAllTextAsync(Path.Combine(directory, "stop-1.0.0.txt"), "stop");
    await WaitForExit(process);
}

void Check(string name, bool condition)
{
    checks++;
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
}

static string Hash(string path)
{
    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    return Convert.ToHexString(SHA256.HashData(file));
}

static string FindRepository()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "tools", "UpdateProbe", "UpdateProbe.csproj"))) return directory.FullName;
    throw new DirectoryNotFoundException("Could not find the updater probe project.");
}
