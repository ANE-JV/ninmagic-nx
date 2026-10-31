using System.Diagnostics;
using System.Text.Json;

namespace Ninmagic.Nx.Updates;

public static class UpdateInstaller
{
    internal static void Start(StagedUpdate staged, string currentVersion)
    {
        if (!UpdateVersions.TryParseStable(currentVersion, out var oldVersion) || !UpdateVersions.TryParseStable(staged.Version, out var newVersion) || newVersion <= oldVersion)
            throw new InvalidDataException("Solo se puede instalar una versión posterior a la actual.");
        var target = Environment.ProcessPath ?? throw new IOException("No se encontró la aplicación actual.");
        UpdateFiles.ValidateInstallTarget(target);
        UpdateFiles.ValidateExecutable(target, currentVersion);
        UpdateFiles.RejectReparseAncestors(staged.FilePath);
        using var source = new FileStream(staged.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length != staged.Size || source.Length > WindowsUpdateService.MaximumAssetBytes) throw new InvalidDataException("El tamaño de la actualización ha cambiado.");
        UpdateFiles.VerifyHash(source, staged.Sha256);
        UpdateFiles.ValidateExecutable(staged.FilePath, staged.Version);
        var parent = Path.GetDirectoryName(target)!;
        using var parentLock = UpdateFiles.LockDirectory(parent);
        var stage = Path.Combine(parent, ".ninmagic-update-" + Guid.NewGuid().ToString("N"));
        try { UpdateFiles.CreateSecureDirectory(stage); }
        catch (UnauthorizedAccessException ex) { throw new IOException("No se puede actualizar en esta carpeta. Abre ninmagic-nx como administrador o muévela a una carpeta propia del PC.", ex); }
        string newExe = Path.Combine(stage, "new.exe"), helper = Path.Combine(stage, "updater.exe"), requestFile = Path.Combine(stage, "install.json");
        using (var output = new FileStream(newExe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { source.CopyTo(output); output.Flush(true); }
        File.Copy(target, helper, overwrite: false);
        var oldHash = UpdateFiles.Hash(helper);
        using var me = Process.GetCurrentProcess();
        var request = new InstallRequest(target, newExe, oldHash, staged.Sha256, currentVersion, staged.Version, me.Id, me.StartTime.ToUniversalTime().Ticks);
        File.WriteAllText(requestFile, JsonSerializer.Serialize(request));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = stage, CreateNoWindow = true };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(requestFile);
        using var process = Process.Start(start) ?? throw new IOException("No se pudo iniciar el instalador de la actualización.");
        var timer = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(stage, "ready.txt")))
        {
            if (process.HasExited || timer.Elapsed > TimeSpan.FromSeconds(15))
            {
                File.WriteAllText(Path.Combine(stage, "cancel.txt"), "cancel");
                throw new IOException("No se pudo preparar el instalador. La aplicación actual sigue abierta y no se ha reemplazado.");
            }
            Thread.Sleep(50);
        }
        // The helper has revalidated everything and now waits for this exact process to exit.
    }

    public static async Task<UpdateInstallResult> RunAsync(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || args.Length != 2 || args[0] != "--apply-update") throw new InvalidDataException("Solicitud de actualización no válida.");
            var requestPath = Path.GetFullPath(args[1]);
            UpdateFiles.RejectReparseAncestors(requestPath);
            var stage = Path.GetDirectoryName(requestPath) ?? throw new IOException("Falta la carpeta de actualización.");
            if (Path.GetFileName(requestPath) != "install.json" || !Path.GetFileName(stage).StartsWith(".ninmagic-update-", StringComparison.Ordinal))
                throw new IOException("La solicitud no está en una carpeta de actualización válida.");
            using var stageLock = UpdateFiles.LockDirectory(stage);
            UpdateFiles.ValidateSecureDirectory(stage);
            if (new FileInfo(requestPath).Length is <= 0 or > 32_768) throw new InvalidDataException("La solicitud de actualización tiene un tamaño no válido.");
            var request = JsonSerializer.Deserialize<InstallRequest>(await File.ReadAllTextAsync(requestPath)) ?? throw new InvalidDataException("Falta la solicitud de actualización.");
            ValidateRequest(request, stage);
            using var parentLock = UpdateFiles.LockDirectory(Path.GetDirectoryName(request.TargetExe)!);
            var helper = Environment.ProcessPath ?? throw new IOException("Falta el ejecutable de actualización.");
            if (!SamePath(helper, Path.Combine(stage, "updater.exe"))) throw new IOException("El instalador no se ejecuta desde su ubicación esperada.");
            if (!string.Equals(UpdateFiles.Hash(helper), request.OriginalSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("El instalador ha cambiado desde su preparación.");
            UpdateFiles.ValidateExecutable(helper, request.CurrentVersion);
            CheckPreparedFiles(request);
            using var parent = FindParent(request);
            await File.WriteAllTextAsync(Path.Combine(stage, "ready.txt"), "ready");
            if (parent is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try { await parent.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { throw new IOException("La aplicación anterior sigue abierta. No se ha reemplazado ningún archivo."); }
            }
            if (File.Exists(Path.Combine(stage, "cancel.txt"))) throw new IOException("La preparación de la actualización fue cancelada; la aplicación actual se conserva.");
            // Mutex ownership is thread-affine. Acquire and dispose without an await in between.
            using (UpdateInstanceGuard.TryAcquire(request.TargetExe) ?? throw new IOException("Hay otra instancia de ninmagic-nx abierta. Cierra esa instancia antes de actualizar."))
            {
                ApplyTransaction(request, path =>
                {
                    var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)!, CreateNoWindow = true };
                    using var installer = Process.GetCurrentProcess();
                    start.ArgumentList.Add("--wait-for-parent"); start.ArgumentList.Add(installer.Id.ToString());
                    start.ArgumentList.Add(installer.StartTime.ToUniversalTime().Ticks.ToString());
                    start.ArgumentList.Add("--updated"); start.ArgumentList.Add(request.NewVersion);
                    using var launched = Process.Start(start) ?? throw new IOException("No se pudo abrir la versión instalada.");
                });
            }
            return new(true, "Actualización instalada. Se conserva el ejecutable anterior como copia de recuperación.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }
    private static Process? FindParent(InstallRequest request)
    {
        Process process;
        try { process = Process.GetProcessById(request.ParentId); }
        catch (ArgumentException) { return null; }
        try
        {
            if (process.StartTime.ToUniversalTime().Ticks != request.ParentStartTimeUtcTicks ||
                !SamePath(process.MainModule?.FileName ?? "", request.TargetExe)) throw new IOException("La aplicación que solicitó actualizar ya no coincide con el proceso esperado.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }
    internal static void ValidateRequest(InstallRequest request, string stage)
    {
        UpdateFiles.ValidateInstallTarget(request.TargetExe);
        if (!SamePath(Path.GetDirectoryName(stage)!, Path.GetDirectoryName(request.TargetExe)!) || !SamePath(request.NewExe, Path.Combine(stage, "new.exe")) ||
            SamePath(request.NewExe, request.TargetExe) || request.ParentId <= 0 || request.ParentStartTimeUtcTicks <= 0 ||
            !UpdateFiles.IsSha256(request.OriginalSha256) || !UpdateFiles.IsSha256(request.NewSha256)) throw new InvalidDataException("Las rutas o la identidad de la actualización no son válidas.");
        if (!UpdateVersions.TryParseStable(request.CurrentVersion, out var current) || !UpdateVersions.TryParseStable(request.NewVersion, out var next) || next <= current)
            throw new InvalidDataException("La versión nueva no es posterior a la actual.");
    }
    private static void CheckPreparedFiles(InstallRequest request)
    {
        UpdateFiles.RejectReparseAncestors(request.TargetExe); UpdateFiles.RejectReparseAncestors(request.NewExe);
        using var oldFile = new FileStream(request.TargetExe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var newFile = new FileStream(request.NewExe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        UpdateFiles.VerifyHash(oldFile, request.OriginalSha256); UpdateFiles.VerifyHash(newFile, request.NewSha256);
        UpdateFiles.ValidateExecutable(request.TargetExe, request.CurrentVersion); UpdateFiles.ValidateExecutable(request.NewExe, request.NewVersion);
    }
    internal static string ApplyTransaction(InstallRequest request, Action<string> launch)
    {
        UpdateFiles.RejectReparseAncestors(request.TargetExe); UpdateFiles.RejectReparseAncestors(request.NewExe);
        var backup = request.TargetExe + ".previous-" + Guid.NewGuid().ToString("N") + ".exe";
        using (var oldFile = new FileStream(request.TargetExe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var newFile = new FileStream(request.NewExe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            UpdateFiles.VerifyHash(oldFile, request.OriginalSha256); UpdateFiles.VerifyHash(newFile, request.NewSha256);
            UpdateFiles.ValidateExecutable(request.TargetExe, request.CurrentVersion); UpdateFiles.ValidateExecutable(request.NewExe, request.NewVersion);
        }
        // ReplaceFile needs writable metadata handles itself. Close the read leases before the
        // atomic swap; the protected stage DACL and directory leases remain held by RunAsync.
        try { File.Replace(request.NewExe, request.TargetExe, backup, ignoreMetadataErrors: true); }
        catch (Exception replaceError)
        {
            // Windows ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 can rename the old file to the
            // backup before failing to move the new one. Restore that exact old image.
            try { RecoverInterruptedReplacement(request.TargetExe, backup, request.OriginalSha256); }
            catch (Exception recoveryError)
            { throw new IOException($"La sustitución se interrumpió. Conserva la copia de recuperación: {backup}", new AggregateException(replaceError, recoveryError)); }
            throw new IOException("No se pudo sustituir la aplicación. El ejecutable anterior se conserva.", replaceError);
        }
        try
        {
            using (var installed = new FileStream(request.TargetExe, FileMode.Open, FileAccess.Read, FileShare.Read))
                UpdateFiles.VerifyHash(installed, request.NewSha256);
            using (var previous = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
                UpdateFiles.VerifyHash(previous, request.OriginalSha256);
            UpdateFiles.ValidateExecutable(request.TargetExe, request.NewVersion);
            launch(request.TargetExe);
        }
        catch (Exception startError)
        {
            var failed = request.TargetExe + ".failed-" + Guid.NewGuid().ToString("N") + ".exe";
            try
            {
                using (var previous = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
                    UpdateFiles.VerifyHash(previous, request.OriginalSha256);
                File.Replace(backup, request.TargetExe, failed, ignoreMetadataErrors: true);
            }
            catch (Exception rollbackError)
            { throw new IOException($"No se pudo abrir la actualización ni restaurar automáticamente la anterior. Conserva la copia de recuperación: {backup}", new AggregateException(startError, rollbackError)); }
            throw new IOException("No se pudo abrir la actualización. Se ha restaurado el ejecutable anterior; puedes abrir ninmagic-nx otra vez.", startError);
        }
        return backup;
    }
    internal static void RecoverInterruptedReplacement(string target, string backup, string originalHash)
    {
        if (File.Exists(target)) return;
        UpdateFiles.RejectReparseAncestors(target); UpdateFiles.RejectReparseAncestors(backup);
        using (var previous = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
            UpdateFiles.VerifyHash(previous, originalHash);
        File.Move(backup, target, overwrite: false);
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
