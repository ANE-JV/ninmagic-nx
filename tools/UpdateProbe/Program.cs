using System.Diagnostics;
using System.Reflection;
using Ninmagic.Nx.Updates;

internal static class Program
{
    // Only disposable executable copies. No UI, cards, or user backup access.
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Environment.GetEnvironmentVariable("NINMAGIC_UPDATE_PROBE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(output) || !Path.IsPathFullyQualified(output) || !Directory.Exists(output)) return 2;
        if (args.FirstOrDefault() == "--apply-update")
        {
            File.WriteAllText(Path.Combine(output, "helper-pid.txt"), Environment.ProcessId.ToString());
            var result = UpdateInstaller.RunAsync(args).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(output, "helper-result.txt"), result.Message);
            return result.Success ? 0 : 1;
        }
        if (args.FirstOrDefault() == "--wait-for-parent")
        {
            if (args.Length != 5 || args[3] != "--updated" || !int.TryParse(args[1], out int parentId) ||
                !long.TryParse(args[2], out var ticks)) return 5;
            try
            {
                using var parent = Process.GetProcessById(parentId);
                if (parent.StartTime.ToUniversalTime().Ticks == ticks && !parent.WaitForExit(30_000)) return 6;
            }
            catch (ArgumentException) { /* Exact old parent already exited. */ }
        }
        using var lease = UpdateInstanceGuard.TryAcquire(Environment.ProcessPath!);
        if (lease is null) return 7;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        File.WriteAllText(Path.Combine(output, "started-" + version + ".txt"),
            $"{Environment.ProcessId}\n{Process.GetCurrentProcess().StartTime.ToUniversalTime():O}\n");
        if (args.FirstOrDefault() == "--probe-install")
        {
            if (args.Length != 4) return 4;
            using var service = new WindowsUpdateService(version);
            service.StartInstall(new StagedUpdate(args[1], args[2], args[3], new FileInfo(args[1]).Length));
            File.WriteAllText(Path.Combine(output, "prepared.txt"), "ready");
        }
        if (args.Contains("--probe-wait") || args.FirstOrDefault() == "--probe-install")
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(output, "stop-" + version + ".txt")))
            {
                if (deadline.Elapsed > TimeSpan.FromMinutes(3)) return 3;
                Thread.Sleep(40);
            }
        }
        return 0;
    }
}
