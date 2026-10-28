using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Ninmagic.Nx.Windows;

internal static class PowerShellRunner
{
    // Parameters are JSON transported inside base64. User strings never become PowerShell code.
    internal static async Task<string> RunAsync(string script, object? parameters, CancellationToken cancellationToken,
        int timeoutSeconds = 120)
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parameters)));
        string fullScript = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
            "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " +
            "$p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + payload + "')) | ConvertFrom-Json; " + script;
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(fullScript)) }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("No se pudo iniciar el servicio de almacenamiento de Windows.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string result = await output.ConfigureAwait(false);
            string detail = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException("Windows no pudo completar la operación de almacenamiento: " + detail.Trim());
            return result.Trim();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (!cancellationToken.IsCancellationRequested)
                throw new IOException("Windows ha tardado demasiado en responder. La operación no se ha completado.");
            throw;
        }
    }
}
