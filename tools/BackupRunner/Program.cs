using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ninmagic.Nx.Core;
using Ninmagic.Nx.Windows;

namespace Ninmagic.Nx.BackupOnly;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        RunnerOptions options;
        try { options = RunnerOptions.Parse(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Uso: ninmagic-nx-backup-only.exe <numeroDisco> <bytesExactos> <sha256MBR> <carpetaCopiaNueva> <progreso.json>");
            return 2;
        }

        ProgressPublisher? publisher = null;
        using var cancellation = new CancellationTokenSource();
        using var stopMonitor = new CancellationTokenSource();
        Task? monitor = null;
        ConsoleCancelEventHandler onCancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            if (!WindowsDiskAccess.IsAdministrator)
                throw new UnauthorizedAccessException("Este ejecutable debe iniciarse con permisos de administrador. No se ha abierto ni modificado la tarjeta.");
            var native = new WindowsDiskAccess();
            IReadOnlyList<DiskInfo> available = await native.ListDisksAsync(cancellation.Token);
            DiskInfo source = available.SingleOrDefault(d => d.Number == options.DiskNumber)
                ?? throw new IOException("El disco original esperado no está disponible entre las tarjetas externas admitidas.");
            if (source.Size != options.ExactSize)
                throw new IOException($"Capacidad distinta de la esperada: {source.Size} bytes. No se ha iniciado la copia.");
            if (source.IsBoot || source.IsSystem)
                throw new IOException("El disco indicado es de Windows o de arranque; se ha bloqueado.");

            // No status file or folder is written before both locations are identified on another physical disk.
            await native.EnsureFolderOnOtherDiskAsync(options.BackupFolder, source, cancellation.Token);
            await native.EnsureFolderOnOtherDiskAsync(options.ProgressPath, source, cancellation.Token);
            SafePaths.RejectReparseAncestors(options.BackupFolder);
            SafePaths.RejectReparseAncestors(options.ProgressPath);
            Directory.CreateDirectory(Path.GetDirectoryName(options.ProgressPath)!);
            publisher = new ProgressPublisher(options, cancellation);
            publisher.Start();
            monitor = WatchCancellationAsync(options.CancelPath, cancellation, stopMonitor.Token);

            source = await native.RefreshAsync(source, cancellation.Token);
            // OpenRead is a read-only physical handle and additionally checks WindowsDiskAccess's selection fingerprint.
            using (Stream input = native.OpenRead(source))
            {
                input.Position = 0;
                var mbr = new byte[512];
                input.ReadExactly(mbr);
                byte[] expected = Convert.FromHexString(options.ExpectedMbrSha256);
                byte[] actual = SHA256.HashData(mbr);
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                    throw new IOException("La huella SHA-256 del MBR no coincide con la tarjeta original comprobada. No se ha iniciado la copia.");
            }
            cancellation.Token.ThrowIfCancellationRequested();
            // The service receives no capability to obtain a writable disk session.
            var service = new BackupService(new BackupReadOnlyAccess(native));
            BackupManifest manifest = await service.CreateAsync(source, options.BackupFolder, publisher, cancellation.Token);
            stopMonitor.Cancel();
            if (monitor is not null) await monitor;
            publisher.Complete(manifest);
            return 0;
        }
        catch (Exception ex)
        {
            stopMonitor.Cancel();
            if (monitor is not null)
            {
                try { await monitor; }
                catch (Exception monitorError) { Console.Error.WriteLine("Monitor de cancelación: " + monitorError.Message); }
            }
            // A bad/unsafe output path is intentionally never written. The launcher's stderr remains available.
            Console.Error.WriteLine(ex.ToString());
            if (publisher is not null)
            {
                try { publisher.Fail(ex); }
                catch (Exception statusError) { Console.Error.WriteLine("No se pudo guardar el estado de error: " + statusError); }
            }
            return ex is OperationCanceledException ? 3 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            stopMonitor.Cancel();
            // The monitor is stopped and awaited before either CancellationTokenSource can be disposed.
            if (monitor is not null)
            {
                try { await monitor; }
                catch (Exception ex) { Console.Error.WriteLine("Fin del monitor: " + ex.Message); }
            }
        }
    }

    private static async Task WatchCancellationAsync(string flag, CancellationTokenSource cancellation, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (File.Exists(flag)) { cancellation.Cancel(); return; }
                await Task.Delay(TimeSpan.FromSeconds(1), stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    /// <summary>Backup-only capability boundary. There is deliberately no route to a writable disk session.</summary>
    private sealed class BackupReadOnlyAccess(WindowsDiskAccess inner) : IDiskAccess
    {
        public Task<IReadOnlyList<DiskInfo>> ListDisksAsync(CancellationToken ct) => inner.ListDisksAsync(ct);
        public Task<DiskInfo> RefreshAsync(DiskInfo expected, CancellationToken ct) => inner.RefreshAsync(expected, ct);
        public Stream OpenRead(DiskInfo disk) => inner.OpenRead(disk);
        public Task EnsureFolderOnOtherDiskAsync(string folder, DiskInfo disk, CancellationToken ct) => inner.EnsureFolderOnOtherDiskAsync(folder, disk, ct);
        public Task<ITargetSession> OpenTargetAsync(DiskInfo disk, CancellationToken ct) =>
            Task.FromException<ITargetSession>(new NotSupportedException("Este ejecutable solo permite copias de lectura; no puede abrir un destino de escritura."));
    }
}

internal sealed record RunnerOptions(int DiskNumber, long ExactSize, string ExpectedMbrSha256, string BackupFolder, string ProgressPath)
{
    public string CancelPath => Path.Combine(Path.GetDirectoryName(ProgressPath)!, "cancel.flag");

    public static RunnerOptions Parse(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException("Se necesitan exactamente cinco argumentos.");
        if (!int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int disk) || disk is < 0 or > 65535)
            throw new ArgumentException("El número de disco no es válido.");
        if (!long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out long size) ||
            size < 64L * 1024 * 1024 || size % 512 != 0 || size / 512 > uint.MaxValue)
            throw new ArgumentException("El tamaño exacto esperado no es válido.");
        if (args[2].Length != 64 || !args[2].All(Uri.IsHexDigit))
            throw new ArgumentException("La huella del MBR debe ser un SHA-256 hexadecimal de 64 caracteres.");
        var backup = LocalPath(args[3]);
        var progress = LocalPath(args[4]);
        if (!Path.GetExtension(progress).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("El archivo de progreso debe terminar en .json.");
        var prefix = backup.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (progress.Equals(backup, StringComparison.OrdinalIgnoreCase) || progress.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("El archivo de progreso debe estar fuera de la carpeta de copia.");
        SafePaths.NormalizeRelative(Path.GetFileName(progress));
        SafePaths.RejectReparseAncestors(backup);
        SafePaths.RejectReparseAncestors(progress);
        return new(disk, size, args[2].ToUpperInvariant(), backup, progress);
    }

    private static string LocalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException("Las rutas deben ser absolutas y locales.");
        var full = Path.GetFullPath(value);
        if (full.Length < 4 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\')
            throw new ArgumentException("Las rutas deben usar una unidad local del PC con letra.");
        return full;
    }
}

internal sealed class ProgressPublisher(RunnerOptions options, CancellationTokenSource cancellation) : IProgress<WorkProgress>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object gate = new();
    private readonly DateTime started = DateTime.UtcNow;
    private long lastTick;
    private WorkProgress latest = new("Preparando copia", "Comprobando la identidad de la tarjeta original", 0, 0);
    private string lastWrittenStage = "";

    public void Start()
    {
        lock (gate) { Write("running", null, null); }
    }

    public void Report(WorkProgress value)
    {
        if (File.Exists(options.CancelPath)) cancellation.Cancel();
        cancellation.Token.ThrowIfCancellationRequested();
        lock (gate)
        {
            latest = value;
            // Stage transitions are immediate. Progress within a stage is limited to one durable update per two seconds.
            if (lastWrittenStage != value.Stage || Environment.TickCount64 - lastTick >= 2000)
                Write("running", null, null);
        }
    }

    public void Complete(BackupManifest manifest)
    {
        lock (gate)
        {
            latest = new("Copia terminada y verificada", "La copia está completa. Ya se puede preparar el cambio de tarjeta.", 1, 1);
            Write("success", null, new
            {
                formatVersion = manifest.FormatVersion,
                appVersion = manifest.AppVersion,
                createdUtc = manifest.CreatedUtc,
                manifestPath = Path.Combine(options.BackupFolder, "manifest.json"),
                fileCount = manifest.Files.Count(f => !f.IsDirectory),
                directoryCount = manifest.Files.Count(f => f.IsDirectory),
                fileBytes = manifest.Files.Sum(f => f.Length),
                rawPartitionCount = manifest.RawPartitions.Count,
                rawBytes = manifest.RawPartitions.Sum(r => r.Partition.Length),
                excludedPaths = manifest.ExcludedPaths
            });
        }
    }

    public void Fail(Exception error)
    {
        lock (gate)
        {
            var canceled = error is OperationCanceledException;
            latest = latest with { Stage = canceled ? "Copia cancelada" : "Error en la copia", Detail = error.Message };
            Write(canceled ? "cancelled" : "error", error.ToString(), null);
        }
    }

    private void Write(string status, string? error, object? summary)
    {
        SafePaths.RejectReparseAncestors(options.ProgressPath);
        var state = new
        {
            status,
            startedUtc = started,
            updatedUtc = DateTime.UtcNow,
            processId = Environment.ProcessId,
            backupOnly = true,
            sourceDiskNumber = options.DiskNumber,
            expectedSourceBytes = options.ExactSize,
            expectedMbrSha256 = options.ExpectedMbrSha256,
            backupFolder = options.BackupFolder,
            cancelFlagPath = options.CancelPath,
            stage = latest.Stage,
            detail = latest.Detail,
            completed = latest.Completed,
            total = latest.Total,
            fraction = latest.Fraction,
            summary,
            error
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var temporary = options.ProgressPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            // Same-directory rename keeps readers from seeing half a JSON document.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, options.ProgressPath, true); break; }
                catch (IOException) when (attempt < 9) { Thread.Sleep(100); }
            }
            lastTick = Environment.TickCount64;
            lastWrittenStage = latest.Stage;
            try { Console.WriteLine(JsonSerializer.Serialize(state)); }
            catch (IOException) { /* status JSON is authoritative when the optional stdout consumer has closed */ }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
