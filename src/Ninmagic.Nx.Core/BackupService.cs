using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ninmagic.Nx.Core;

/// <summary>Two-phase migration. Source handles are read-only; only a verified backup can reach the writer.</summary>
public sealed class BackupService
{
    private readonly IDiskAccess disks;

    public BackupService(IDiskAccess disks)
    {
        this.disks = disks ?? throw new ArgumentNullException(nameof(disks));
    }

    private const int BufferSize = 4 * 1024 * 1024;
    private const int MaxManifestBytes = 64 * 1024 * 1024;
    private const int MaxEntries = 250_000;
    private const string ManifestName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = false };
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> WindowsFolders = new(PathComparer) { "System Volume Information", "$RECYCLE.BIN" };
    private const FileAttributes PortableAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden |
        FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    public async Task<BackupManifest> CreateAsync(DiskInfo source, string backupFolder,
        IProgress<WorkProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        backupFolder = Path.GetFullPath(backupFolder);
        SafePaths.RejectReparseAncestors(backupFolder);
        source = await disks.RefreshAsync(source, cancellationToken);
        if (source.IsBoot || source.IsSystem)
            throw new InvalidOperationException("El origen debe ser una microSD; no se admiten discos de Windows ni de arranque.");
        await disks.EnsureFolderOnOtherDiskAsync(backupFolder, source, cancellationToken);
        if (Directory.Exists(backupFolder) && Directory.EnumerateFileSystemEntries(backupFolder).Any())
            throw new IOException("Elige una carpeta de copia nueva o vacía. Las copias anteriores nunca se sobrescriben.");
        if (File.Exists(backupFolder)) throw new IOException("El destino de copia debe ser una carpeta.");

        using var sourceStream = disks.OpenRead(source);
        var layout = DiskLayout.Inspect(sourceStream, source);
        var volumes = source.Volumes.Where(v => v.Offset == layout.Data.Offset && v.Size == layout.Data.Length).ToArray();
        if (volumes.Length != 1 || string.IsNullOrWhiteSpace(volumes[0].Root) || !Directory.Exists(volumes[0].Root))
            throw new InvalidDataException("La partición de archivos debe tener una única letra de unidad accesible en Windows.");
        var volume = volumes[0];
        if (!string.Equals(volume.FileSystem, layout.FileSystem, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("El sistema de archivos detectado no coincide con el que muestra Windows.");
        SafePaths.RejectReparseAncestors(volume.Root);
        var rawLayout = layout.Raw.Select(raw => raw.Partition).ToArray();
        var operationProgress = new OperationProgress(progress, "Crear copia", BackupSteps(rawLayout, 0));
        operationProgress.Report(1, "Analizando", "Inventario de archivos y particiones emuMMC");
        var excluded = new List<string>();
        var inventory = Inventory(volume.Root, excluded, cancellationToken);
        var rawBytes = layout.Raw.Sum(p => p.Partition.Length);
        var fileBytes = inventory.Sum(f => f.Length);
        var totalBytes = checked(fileBytes + rawBytes);
        operationProgress = new OperationProgress(progress, "Crear copia", BackupSteps(rawLayout, fileBytes));
        operationProgress.Report(1, "Analizando", "Inventario completo; comprobando espacio para la copia");
        EnsureLocalFreeSpace(backupFolder, checked(totalBytes + 64L * 1024 * 1024));

        Directory.CreateDirectory(backupFolder);
        var fileRoot = Path.Combine(backupFolder, "files");
        var rawRoot = Path.Combine(backupFolder, "raw");
        Directory.CreateDirectory(fileRoot);
        Directory.CreateDirectory(rawRoot);
        var incomplete = Path.Combine(backupFolder, "COPIA-INCOMPLETA.txt");
        await File.WriteAllTextAsync(incomplete,
            "Esta copia no está terminada. Solo manifest.json confirma una copia completada y verificada. La tarjeta original no se ha modificado.", cancellationToken);

        var rawParts = new List<RawPartition>();
        foreach (var raw in layout.Raw)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = $"raw/partition-{raw.Partition.Index}.img";
            var outputPath = SafePaths.Resolve(backupFolder, relative);
            sourceStream.Position = raw.Partition.Offset;
            string hash;
            await using (var output = NewOutput(outputPath))
            {
                hash = await CopyAndHashAsync(sourceStream, output, raw.Partition.Length,
                    n => operationProgress.Report(rawParts.Count + 2, "Creando copia", relative, n, raw.Partition.Length), cancellationToken);
                output.Flush(true);
            }
            rawParts.Add(new(raw.Partition, raw.EmuSector, relative, hash));
        }

        var filesStep = rawParts.Count + 2;
        long completed = 0;
        operationProgress.Report(filesStep, "Creando copia", "Copiando carpetas y archivos al PC", 0, fileBytes);
        var files = new List<FileEntry>(inventory.Count);
        foreach (var entry in inventory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = SafePaths.Resolve(fileRoot, entry.Path);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                files.Add(entry);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var original = SafePaths.Resolve(volume.Root, entry.Path);
            AssertUnchanged(original, entry);
            string hash;
            await using (var input = ReadFile(original))
            await using (var output = NewOutput(destination))
            {
                if (input.Length != entry.Length) throw Changed(entry.Path);
                hash = await CopyAndHashAsync(input, output, entry.Length,
                    n => operationProgress.Report(filesStep, "Creando copia", entry.Path, completed + n, fileBytes), cancellationToken);
                if (input.Length != entry.Length) throw Changed(entry.Path);
                output.Flush(true);
            }
            AssertUnchanged(original, entry);
            completed += entry.Length;
            files.Add(entry with { Sha256 = hash });
        }

        operationProgress.Report(filesStep + 1, "Comprobando origen", "Comprobando que la tarjeta original no ha cambiado");
        var after = Inventory(volume.Root, new List<string>(), cancellationToken);
        if (after.Count != inventory.Count || !after.SequenceEqual(inventory))
            throw new IOException("La tarjeta cambió durante la copia. No se completó la copia; cierra los programas que la utilizan y repite en una carpeta nueva.");

        var manifest = new BackupManifest(1, "1.3.0", DateTime.UtcNow, source, layout.Data,
            layout.FileSystem, volume.Label, rawParts, files, excluded);
        ValidateManifest(manifest);
        operationProgress.Report(filesStep + 2, "Verificando copia", "Comprobando los datos guardados en el PC", 0, totalBytes);
        await VerifyContentsAsync(backupFolder, manifest, operationProgress.ForStep(filesStep + 2), cancellationToken);
        EmuMmc.ValidateReferences(manifest, backupFolder);
        // Commit last. A cancellation, disconnect or failed hash never produces a completed manifest.
        operationProgress.Report(filesStep + 3, "Guardando manifiesto", "Confirmando y guardando la copia verificada");
        var pending = Path.Combine(backupFolder, "manifest.pending.json");
        await using (var output = NewOutput(pending))
        {
            await JsonSerializer.SerializeAsync(output, manifest, JsonOptions, cancellationToken);
            if (output.Length > MaxManifestBytes) throw new InvalidDataException("El inventario es demasiado grande para el formato de copia de esta versión.");
            output.Flush(true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(pending, Path.Combine(backupFolder, ManifestName), false);
        File.Delete(incomplete);
        operationProgress.Report(filesStep + 3, "Copia verificada", $"{files.Count(f => !f.IsDirectory)} archivos y {rawParts.Count} particiones emuMMC",
            isOperationComplete: true);
        return manifest;
    }

    public async Task<BackupManifest> LoadAndVerifyAsync(string folder,
        IProgress<WorkProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        folder = Path.GetFullPath(folder);
        var manifest = await LoadManifestAsync(folder, cancellationToken);
        var totalBytes = BackupBytes(manifest);
        var operationProgress = VerificationProgress(progress, totalBytes);
        await VerifyBackupAsync(folder, manifest, operationProgress, 1, cancellationToken);
        operationProgress.Report(1, "Copia verificada", "Todos los SHA-256 y las referencias emuMMC son válidos",
            totalBytes, totalBytes, isOperationComplete: true);
        return manifest;
    }

    private static async Task<BackupManifest> LoadManifestAsync(string folder, CancellationToken cancellationToken)
    {
        folder = Path.GetFullPath(folder);
        SafePaths.RejectReparseAncestors(folder);
        var path = SafePaths.Resolve(folder, ManifestName);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 2 || info.Length > MaxManifestBytes)
            throw new InvalidDataException("Falta manifest.json o su tamaño no es válido. La copia puede estar incompleta.");
        BackupManifest manifest;
        try
        {
            await using var input = ReadFile(path);
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(input, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("El manifiesto está vacío.");
        }
        catch (JsonException ex) { throw new InvalidDataException("El manifiesto de la copia está dañado.", ex); }
        ValidateManifest(manifest);
        return manifest;
    }

    public async Task<MigrationPlan> PlanAsync(string backupFolder, DiskInfo target,
        IProgress<WorkProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        backupFolder = Path.GetFullPath(backupFolder);
        var manifest = await LoadManifestAsync(backupFolder, cancellationToken);
        var totalBytes = BackupBytes(manifest);
        var operationProgress = VerificationProgress(progress, totalBytes);
        await VerifyBackupAsync(backupFolder, manifest, operationProgress, 1, cancellationToken);
        target = await disks.RefreshAsync(target, cancellationToken);
        await disks.EnsureFolderOnOtherDiskAsync(backupFolder, target, cancellationToken);
        ValidateDestination(manifest, target);
        var plan = DiskLayout.Plan(manifest, target);
        operationProgress.Report(1, "Copia verificada", "Copia comprobada y plan de traslado preparado",
            totalBytes, totalBytes, isOperationComplete: true);
        return plan;
    }

    public async Task RestoreAsync(string backupFolder, DiskInfo target,
        IProgress<WorkProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        backupFolder = Path.GetFullPath(backupFolder);
        // Repeat validation after confirmation. A previous plan never grants permission to stale hardware.
        var manifest = await LoadManifestAsync(backupFolder, cancellationToken);
        var operationProgress = new OperationProgress(progress, "Restaurar tarjeta", RestoreSteps(manifest));
        await VerifyBackupAsync(backupFolder, manifest, operationProgress, 1, cancellationToken);
        target = await disks.RefreshAsync(target, cancellationToken);
        await disks.EnsureFolderOnOtherDiskAsync(backupFolder, target, cancellationToken);
        ValidateDestination(manifest, target);
        var plan = DiskLayout.Plan(manifest, target);
        var statusPath = SafePaths.Resolve(backupFolder, "ultima-restauracion.json");
        await WriteStatusAsync(statusPath, "iniciada", target, null);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var session = await disks.OpenTargetAsync(target, cancellationToken))
            {
            var stream = session.Stream;
            cancellationToken.ThrowIfCancellationRequested();
            operationProgress.Report(2, "Preparando destino", "Creando particiones en la tarjeta de destino");
            cancellationToken.ThrowIfCancellationRequested();
            DiskLayout.WriteMbr(stream, plan);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(plan.FileSystem, "FAT32", StringComparison.OrdinalIgnoreCase))
                Fat32Formatter.Format(stream, plan.DataPartition, manifest.VolumeLabel, cancellationToken);

            for (var i = 0; i < manifest.RawPartitions.Count; i++)
            {
                var raw = manifest.RawPartitions[i];
                var destination = plan.RawPartitions[i];
                await using var input = ReadFile(SafePaths.Resolve(backupFolder, raw.ImageFile));
                if (input.Length != raw.Partition.Length) throw new InvalidDataException("Una imagen emuMMC cambió después de verificarla.");
                stream.Position = destination.Offset;
                var hash = await CopyAndHashAsync(input, stream, raw.Partition.Length,
                    n => operationProgress.Report(3 + 2 * i, "Restaurando emuMMC", raw.ImageFile, n, raw.Partition.Length), cancellationToken);
                if (!HashMatches(hash, raw.Sha256)) throw new InvalidDataException("La imagen emuMMC cambió durante la restauración.");
                FlushPhysical(stream);
                stream.Position = destination.Offset;
                var actual = await HashAsync(stream, destination.Length,
                    n => operationProgress.Report(4 + 2 * i, "Verificando emuMMC", raw.ImageFile, n, destination.Length), cancellationToken);
                if (!HashMatches(actual, raw.Sha256))
                    throw new IOException("La tarjeta de destino no conserva los datos emuMMC escritos. No la uses; conserva la original y la copia.");
            }
            var mountStep = 3 + 2 * manifest.RawPartitions.Count;
            operationProgress.Report(mountStep, "Montando destino", "Confirmando la escritura y montando la partición de archivos");
            FlushPhysical(stream);
            cancellationToken.ThrowIfCancellationRequested();
            var targetRoot = await session.MountDataAsync(plan, cancellationToken);
            if (!Directory.Exists(targetRoot)) throw new IOException("Windows no ha montado la partición nueva.");
            SafePaths.RejectReparseAncestors(targetRoot);
            var fileRoot = SafePaths.Resolve(backupFolder, "files");
            var fileTotal = manifest.Files.Sum(f => f.Length);
            long fileDone = 0;
            operationProgress.Report(mountStep + 1, "Restaurando archivos", "Creando carpetas y copiando archivos", 0, fileTotal);
            foreach (var entry in manifest.Files.OrderBy(f => f.IsDirectory ? 0 : 1).ThenBy(f => f.Path, PathComparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPath = SafePaths.Resolve(targetRoot, entry.Path);
                if (entry.IsDirectory) { Directory.CreateDirectory(outputPath); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var input = ReadFile(SafePaths.Resolve(fileRoot, entry.Path));
                await using var output = NewOutput(outputPath);
                if (input.Length != entry.Length) throw new InvalidDataException($"El archivo de copia cambió: {entry.Path}");
                var actual = await CopyAndHashAsync(input, output, entry.Length,
                    n => operationProgress.Report(mountStep + 1, "Restaurando archivos", entry.Path, fileDone + n, fileTotal), cancellationToken);
                output.Flush(true);
                if (!HashMatches(actual, entry.Sha256)) throw new InvalidDataException($"El archivo de copia cambió: {entry.Path}");
                fileDone += entry.Length;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var patched = EmuMmc.PatchReferences(targetRoot, plan);
            var known = manifest.Files.Where(f => !f.IsDirectory).ToDictionary(f => f.Path, PathComparer);
            foreach (var patch in patched)
            {
                var relative = SafePaths.NormalizeRelative(patch.Key);
                if (!known.ContainsKey(relative) || patch.Value is null || patch.Value.Length > 1024 * 1024)
                    throw new InvalidDataException("Se intentó cambiar una referencia emuMMC que no pertenece a la copia.");
            }
            fileDone = 0;
            operationProgress.Report(mountStep + 2, "Verificando archivos", "Comprobando los archivos de la tarjeta nueva", 0, fileTotal);
            foreach (var entry in manifest.Files.Where(f => !f.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = SafePaths.Resolve(targetRoot, entry.Path);
                var expectedHash = entry.Sha256;
                var expectedLength = entry.Length;
                if (patched.TryGetValue(entry.Path, out var bytes))
                {
                    expectedHash = Convert.ToHexString(SHA256.HashData(bytes));
                    expectedLength = bytes.LongLength;
                }
                await using (var input = ReadFile(path))
                {
                    if (input.Length != expectedLength) throw new IOException($"Tamaño incorrecto en destino: {entry.Path}");
                    var actual = await HashAsync(input, input.Length,
                        n => operationProgress.Report(mountStep + 2, "Verificando archivos", entry.Path, fileDone + n, fileTotal), cancellationToken);
                    if (!HashMatches(actual, expectedHash)) throw new IOException($"Error de verificación en destino: {entry.Path}");
                }
                ApplyMetadata(path, entry);
                fileDone += entry.Length;
            }
            // Creating children changes directory timestamps and archive flags. Restore these last, deepest first.
            operationProgress.Report(mountStep + 3, "Finalizando traslado", "Conservando atributos y fechas de las carpetas");
            foreach (var entry in manifest.Files.Where(f => f.IsDirectory).OrderByDescending(f => f.Path.Count(c => c == '/')))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyMetadata(SafePaths.Resolve(targetRoot, entry.Path), entry);
            }
            operationProgress.Report(mountStep + 3, "Finalizando traslado", "Confirmando que Windows ha terminado de escribir la tarjeta");
            } // Dispose performs the final volume flush; success is recorded only after it succeeds.
            await WriteStatusAsync(statusPath, "completada-y-verificada", target, null);
            operationProgress.Report(6 + 2 * manifest.RawPartitions.Count, "Migración verificada",
                "Expulsa la tarjeta de forma segura y comprueba el arranque en la misma consola. Conserva la original y la copia.",
                isOperationComplete: true);
        }
        catch (Exception ex)
        {
            await WriteStatusAsync(statusPath, ex is OperationCanceledException ? "cancelada-destino-incompleto" : "error-destino-incompleto", target, ex.Message);
            throw;
        }
    }

    private static long BackupBytes(BackupManifest manifest) =>
        checked(manifest.Files.Sum(file => file.Length) + manifest.RawPartitions.Sum(raw => raw.Partition.Length));

    private static IReadOnlyList<WorkStep> BackupSteps(IReadOnlyList<PartitionInfo> rawParts, long fileBytes)
    {
        var steps = new List<WorkStep> { new("analyze", "Analizar los archivos de la tarjeta original", 0, WorkRateKind.None) };
        for (var i = 0; i < rawParts.Count; i++)
            steps.Add(new($"copy-raw-{rawParts[i].Index}", $"Copiar emuMMC {i + 1} de {rawParts.Count} al PC",
                rawParts[i].Length, WorkRateKind.SourceRead));
        steps.Add(new("copy-files", "Copiar los archivos al PC", fileBytes, WorkRateKind.SourceRead));
        steps.Add(new("check-source", "Comprobar que la tarjeta original no ha cambiado", 0, WorkRateKind.None));
        steps.Add(new("verify-backup", "Verificar la copia guardada en el PC",
            checked(fileBytes + rawParts.Sum(raw => raw.Length)), WorkRateKind.PcRead));
        steps.Add(new("save-manifest", "Guardar y confirmar la copia terminada", 0, WorkRateKind.None));
        return steps;
    }

    private static IReadOnlyList<WorkStep> RestoreSteps(BackupManifest manifest)
    {
        var steps = new List<WorkStep>
        {
            new("verify-backup", "Verificar la copia del PC antes de borrar", BackupBytes(manifest), WorkRateKind.PcRead),
            new("prepare-target", "Preparar las particiones de la tarjeta nueva", 0, WorkRateKind.None)
        };
        for (var i = 0; i < manifest.RawPartitions.Count; i++)
        {
            var raw = manifest.RawPartitions[i];
            steps.Add(new($"copy-raw-{raw.Partition.Index}", $"Copiar emuMMC {i + 1} de {manifest.RawPartitions.Count} a la tarjeta",
                raw.Partition.Length, WorkRateKind.TargetWrite));
            steps.Add(new($"verify-raw-{raw.Partition.Index}", $"Verificar emuMMC {i + 1} de {manifest.RawPartitions.Count} en la tarjeta",
                raw.Partition.Length, WorkRateKind.TargetRead));
        }
        var fileBytes = manifest.Files.Sum(file => file.Length);
        steps.Add(new("mount-target", "Montar la partición de archivos", 0, WorkRateKind.None));
        steps.Add(new("copy-files", "Copiar los archivos a la tarjeta nueva", fileBytes, WorkRateKind.TargetWrite));
        steps.Add(new("verify-files", "Verificar los archivos de la tarjeta nueva", fileBytes, WorkRateKind.TargetRead));
        steps.Add(new("finalize", "Conservar atributos y confirmar la escritura final", 0, WorkRateKind.None));
        return steps;
    }

    private static OperationProgress VerificationProgress(IProgress<WorkProgress> progress, long bytes) =>
        new(progress, "Comprobar copia", new[] { new WorkStep("verify-backup", "Comprobar la copia del PC", bytes, WorkRateKind.PcRead) });

    private static async Task VerifyBackupAsync(string folder, BackupManifest manifest,
        OperationProgress progress, int stepNumber, CancellationToken cancellationToken)
    {
        progress.Report(stepNumber, "Verificando copia", "Comprobando la copia guardada en el PC", 0, BackupBytes(manifest));
        await VerifyContentsAsync(folder, manifest, progress.ForStep(stepNumber), cancellationToken);
        EmuMmc.ValidateReferences(manifest, folder);
    }

    public static void ValidateDestination(BackupManifest manifest, DiskInfo target)
    {
        if (target.IsBoot || target.IsSystem || target.IsReadOnly)
            throw new InvalidOperationException("El destino es un disco de Windows, de arranque o protegido contra escritura.");
        if (target.LogicalSectorSize != 512 || target.Size < manifest.Source.Size)
            throw new InvalidOperationException("El destino debe tener sectores de 512 bytes y una capacidad real igual o mayor que la tarjeta original.");
        var source = manifest.Source;
        if (target.Size == source.Size &&
            ((Meaningful(target.SerialNumber) && string.Equals(source.SerialNumber.Trim(), target.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase)) ||
             (Meaningful(target.UniqueId) && string.Equals(source.UniqueId.Trim(), target.UniqueId.Trim(), StringComparison.OrdinalIgnoreCase)) ||
             target.Identity == source.Identity))
            throw new InvalidOperationException("El destino parece ser la tarjeta original. Se ha bloqueado para protegerla.");
    }

    private static bool Meaningful(string value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Any(c => c is not '0' and not '-' and not ' ');

    public static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest is null || manifest.FormatVersion != 1 || manifest.Source is null || manifest.DataPartition is null ||
            manifest.Files is null || manifest.RawPartitions is null || manifest.ExcludedPaths is null ||
            manifest.Files.Count > MaxEntries || manifest.RawPartitions.Count > 3 ||
            manifest.Source.LogicalSectorSize != 512 || manifest.Source.Size <= 0 || manifest.Source.Size % 512 != 0 ||
            manifest.Source.Size / 512 > uint.MaxValue || manifest.Source.SerialNumber is null || manifest.Source.UniqueId is null ||
            manifest.Source.BusType is null || manifest.Source.Volumes is null ||
            manifest.FileSystem is not ("FAT32" or "exFAT") || manifest.VolumeLabel is null || manifest.VolumeLabel.Length > 64)
            throw new InvalidDataException("El manifiesto no tiene un formato o una estructura de disco admitidos.");
        var partitions = new List<PartitionInfo> { manifest.DataPartition };
        var imageNames = new HashSet<string>(PathComparer);
        foreach (var raw in manifest.RawPartitions)
        {
            if (raw is null || raw.Partition is null || !ValidHash(raw.Sha256)) throw new InvalidDataException("Metadatos emuMMC no válidos.");
            var name = SafePaths.NormalizeRelative(raw.ImageFile);
            if (name != $"raw/partition-{raw.Partition.Index}.img" || !imageNames.Add(name))
                throw new InvalidDataException("Ruta de imagen emuMMC no válida o repetida.");
            if (raw.EmuSector < raw.Partition.StartSector || (long)raw.EmuSector + 0x4002 >= (long)raw.Partition.StartSector + raw.Partition.SectorCount)
                throw new InvalidDataException("La emuMMC queda fuera de su partición.");
            partitions.Add(raw.Partition);
        }
        if (manifest.DataPartition.Index != 1 ||
            (manifest.FileSystem == "FAT32" && manifest.DataPartition.Type is not (0x0b or 0x0c)) ||
            (manifest.FileSystem == "exFAT" && manifest.DataPartition.Type != 0x07))
            throw new InvalidDataException("La partición de archivos no coincide con el manifiesto.");
        var indices = new HashSet<int>();
        foreach (var part in partitions)
        {
            if (part.Index < 1 || part.Index > 4 || !indices.Add(part.Index) || part.StartSector == 0 || part.SectorCount == 0 ||
                part.Offset + part.Length > manifest.Source.Size)
                throw new InvalidDataException("Una partición está repetida o fuera del disco.");
        }
        var sorted = partitions.OrderBy(p => p.StartSector).ToArray();
        for (var i = 1; i < sorted.Length; i++)
            if (sorted[i - 1].Offset + sorted[i - 1].Length > sorted[i].Offset)
                throw new InvalidDataException("Hay particiones solapadas en la copia.");
        var names = new Dictionary<string, FileEntry>(PathComparer);
        long total = 0;
        foreach (var entry in manifest.Files)
        {
            if (entry is null) throw new InvalidDataException("Entrada vacía en el inventario.");
            var name = SafePaths.NormalizeRelative(entry.Path);
            if (name != entry.Path || !names.TryAdd(name, entry) || entry.Length < 0 ||
                (entry.IsDirectory && (entry.Length != 0 || entry.Sha256 != "")) ||
                (!entry.IsDirectory && !ValidHash(entry.Sha256)) ||
                (entry.Attributes & ~(int)(PortableAttributes | FileAttributes.Directory | FileAttributes.Normal)) != 0 ||
                entry.IsDirectory != (((FileAttributes)entry.Attributes & FileAttributes.Directory) != 0))
                throw new InvalidDataException($"Entrada de inventario no válida o repetida: {name}");
            if (WindowsFolders.Contains(name.Split('/')[0])) throw new InvalidDataException("La copia incluye una carpeta reservada de Windows.");
            total = checked(total + entry.Length);
        }
        if (total > manifest.DataPartition.Length) throw new InvalidDataException("El tamaño de los archivos supera la partición original.");
        foreach (var entry in names.Values)
        {
            var split = entry.Path.LastIndexOf('/');
            if (split >= 0 && (!names.TryGetValue(entry.Path[..split], out var parent) || !parent.IsDirectory))
                throw new InvalidDataException("El inventario omite una carpeta padre o la presenta como archivo.");
        }
        if (manifest.ExcludedPaths.Count > 2 || manifest.ExcludedPaths.Any(p => !WindowsFolders.Contains(p)))
            throw new InvalidDataException("La copia declara exclusiones no admitidas.");
    }

    private static List<FileEntry> Inventory(string root, List<string> excluded, CancellationToken ct)
    {
        var result = new List<FileEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        var names = new HashSet<string>(PathComparer);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var relative = SafePaths.NormalizeRelative(Path.GetRelativePath(root, path));
                var attrs = File.GetAttributes(path);
                if (directory == root && WindowsFolders.Contains(Path.GetFileName(path)) && (attrs & FileAttributes.Directory) != 0)
                { excluded.Add(Path.GetFileName(path)); continue; }
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"La tarjeta contiene un enlace no admitido: {relative}");
                if ((attrs & ~(PortableAttributes | FileAttributes.Directory | FileAttributes.Normal)) != 0)
                    throw new InvalidDataException($"El archivo tiene atributos no admitidos: {relative}");
                if (!names.Add(relative) || result.Count >= MaxEntries) throw new InvalidDataException("Hay rutas repetidas o demasiados archivos en la tarjeta.");
                var isDirectory = (attrs & FileAttributes.Directory) != 0;
                var length = isDirectory ? 0 : new FileInfo(path).Length;
                result.Add(new(relative, length, "", (int)attrs, File.GetLastWriteTimeUtc(path), isDirectory));
                if (isDirectory) pending.Push(path);
            }
        }
        result.Sort((a, b) => PathComparer.Compare(a.Path, b.Path));
        excluded.Sort(PathComparer);
        return result;
    }

    private static void AssertUnchanged(string path, FileEntry entry)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc != entry.LastWriteTimeUtc || (int)info.Attributes != entry.Attributes)
            throw Changed(entry.Path);
    }
    private static IOException Changed(string path) => new($"Un archivo cambió durante la copia: {path}. Cierra otros programas y vuelve a intentarlo.");

    private static async Task VerifyContentsAsync(string folder, BackupManifest manifest, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        var total = checked(manifest.Files.Sum(f => f.Length) + manifest.RawPartitions.Sum(r => r.Partition.Length));
        long completed = 0;
        var fileRoot = SafePaths.Resolve(folder, "files");
        if (!Directory.Exists(fileRoot)) throw new InvalidDataException("Falta la carpeta de archivos de la copia.");
        foreach (var entry in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = SafePaths.Resolve(fileRoot, entry.Path);
            if (entry.IsDirectory)
            {
                if (!Directory.Exists(path)) throw new InvalidDataException($"Falta una carpeta de la copia: {entry.Path}");
                continue;
            }
            await using var input = ReadFile(path);
            if (input.Length != entry.Length) throw new InvalidDataException($"Tamaño incorrecto en la copia: {entry.Path}");
            var actual = await HashAsync(input, input.Length,
                n => progress.Report(new("Verificando copia", entry.Path, completed + n, total)), ct);
            if (!HashMatches(actual, entry.Sha256)) throw new InvalidDataException($"SHA-256 incorrecto en la copia: {entry.Path}");
            completed += entry.Length;
        }
        foreach (var raw in manifest.RawPartitions)
        {
            ct.ThrowIfCancellationRequested();
            await using var input = ReadFile(SafePaths.Resolve(folder, raw.ImageFile));
            if (input.Length != raw.Partition.Length) throw new InvalidDataException($"Tamaño incorrecto en la imagen {raw.ImageFile}");
            var actual = await HashAsync(input, input.Length,
                n => progress.Report(new("Verificando copia", raw.ImageFile, completed + n, total)), ct);
            if (!HashMatches(actual, raw.Sha256)) throw new InvalidDataException($"SHA-256 incorrecto en la imagen {raw.ImageFile}");
            completed += raw.Partition.Length;
        }
        progress.Report(new("Copia verificada", "Todos los SHA-256 coinciden", total, total));
    }

    private static FileStream ReadFile(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static FileStream NewOutput(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static void FlushPhysical(Stream stream) { if (stream is FileStream file) file.Flush(true); else stream.Flush(); }

    internal static Task<string> HashAsync(Stream input, long length, Action<long>? progress, CancellationToken ct) =>
        CopyAndHashAsync(input, null, length, progress, ct);

    internal static async Task<string> CopyAndHashAsync(Stream input, Stream? output, long length, Action<long>? progress, CancellationToken ct)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long copied = 0;
            var lastReport = Environment.TickCount64;
            progress?.Invoke(0);
            while (copied < length)
            {
                ct.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(BufferSize, length - copied);
                var read = await input.ReadAsync(buffer.AsMemory(0, wanted), ct);
                if (read == 0) throw new EndOfStreamException("La lectura terminó antes de tiempo. Revisa el lector y la tarjeta.");
                hash.AppendData(buffer, 0, read);
                if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), ct);
                copied += read;
                if (Environment.TickCount64 - lastReport >= 150 || copied == length)
                { progress?.Invoke(copied); lastReport = Environment.TickCount64; }
            }
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(copied);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool HashMatches(string actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static void ApplyMetadata(string path, FileEntry entry)
    {
        var attributes = (FileAttributes)entry.Attributes & PortableAttributes;
        if (entry.IsDirectory) Directory.SetLastWriteTimeUtc(path, entry.LastWriteTimeUtc);
        else File.SetLastWriteTimeUtc(path, entry.LastWriteTimeUtc);
        File.SetAttributes(path, attributes == 0 ? FileAttributes.Normal : attributes);
        var actual = File.GetAttributes(path) & PortableAttributes;
        if (actual != attributes) throw new IOException($"No se conservaron los atributos de {entry.Path}");
    }

    private static void EnsureLocalFreeSpace(string folder, long required)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Guarda la copia en una unidad local del PC.");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.AvailableFreeSpace < required)
            throw new IOException($"No hay espacio suficiente en el PC. Se necesitan aproximadamente {required / 1_000_000_000.0:0.00} GB libres.");
        if (string.Equals(drive.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase))
            throw new IOException("La copia debe guardarse en una unidad NTFS o exFAT del PC; FAT32 no permite las imágenes grandes de emuMMC.");
    }

    private static async Task WriteStatusAsync(string path, string state, DiskInfo target, string? error)
    {
        // A diagnostic failure must not hide the original I/O failure or turn an incomplete restore into success.
        try
        {
            SafePaths.RejectReparseAncestors(path);
            var text = JsonSerializer.Serialize(new { state, timeUtc = DateTime.UtcNow, target = new { target.Number, target.FriendlyName, target.Size }, error }, JsonOptions);
            await File.WriteAllTextAsync(path, text, CancellationToken.None);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
    }

}
