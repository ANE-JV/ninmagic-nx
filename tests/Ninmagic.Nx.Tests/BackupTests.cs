using Ninmagic.Nx.Core;
using System.Buffers.Binary;
using System.Text.Json;

namespace Ninmagic.Nx.Tests;

public static class BackupTests
{
    private static readonly IProgress<WorkProgress> Quiet = new ImmediateProgress(_ => { });
    public static async Task<int> RunAsync()
    {
        var cases = 0;
        foreach (var bad in new[] { "../escape", "a/../escape", "C:/escape", @"\\server\share", "/root", "a//b", "file:stream", "CON", "nul.txt", "COM¹.log", "CONIN$", "CON .txt", "file.", "a/ ", "a?b", "a\u0001b" })
        {
            ExpectThrows<InvalidDataException>(() => SafePaths.NormalizeRelative(bad));
            cases++;
        }
        Check(SafePaths.NormalizeRelative(@"emuMMC\RAW1\raw_based") == "emuMMC/RAW1/raw_based", "Normalización de separadores");
        cases++;
        var root = Path.Combine(Path.GetTempPath(), "ninmagic-nx-backup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var targetRoot = Path.Combine(root, "target");
            var backup = Path.Combine(root, "backup");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(Path.Combine(sourceRoot, "Nintendo", "Contents", "split.nca"));
            Directory.CreateDirectory(Path.Combine(sourceRoot, "switch"));
            Directory.CreateDirectory(Path.Combine(sourceRoot, "emuMMC", "RAW1"));
            Directory.CreateDirectory(Path.Combine(sourceRoot, "$RECYCLE.BIN"));
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "$RECYCLE.BIN", "discarded.txt"), "Windows housekeeping");
            await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "switch", "prueba.nro"), Enumerable.Range(0, 8001).Select(i => (byte)(i * 7)).ToArray());
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "Nintendo", "Contents", "split.nca", "00"), "Contenido de prueba, sin datos de consola.");
            await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "empty.bin"), []);
            var rawPart = new PartitionInfo(2, 0xE0, (uint)(4L * 1024 * 1024 * 1024 / 512 - 2048 - 131072), 131072);
            var data = new PartitionInfo(1, 0x0C, 32768, rawPart.StartSector - 32768);
            uint emuSector = rawPart.StartSector + 0x8000;
            await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "emuMMC", "RAW1", "raw_based"), BitConverter.GetBytes(emuSector));
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "emuMMC", "emummc.ini"),
                $"[emummc]\nenabled=1\nsector=0x{emuSector:X}\npath=emuMMC/RAW1\nid=0x1234\nnintendo_path=emuMMC/RAW1/Nintendo\n");
            var source = new DiskInfo(90, "Simulación origen", "SOURCE", "SOURCE-ID", 4L * 1024 * 1024 * 1024,
                512, "USB", false, false, false, "MBR", [new(1, data.Offset, data.Length, sourceRoot, "FAT32", "NINMAGIC")]);
            var target = source with { Number = 91, FriendlyName = "Simulación destino", SerialNumber = "TARGET", UniqueId = "TARGET-ID", Size = 8L * 1024 * 1024 * 1024, Volumes = [] };
            var fake = new FakeDisks(source, target, targetRoot);
            DiskLayout.WriteMbr(fake.SourceStream, new(data, [rawPart], new Dictionary<uint, uint>(), 0, "FAT32", source.Size));
            Fat32Formatter.Format(fake.SourceStream, data, "NINMAGIC", CancellationToken.None);
            WriteSyntheticNand(fake.SourceStream, emuSector);
            var nested = Path.Combine(sourceRoot, "Nintendo", "Contents", "split.nca");
            File.SetAttributes(nested, FileAttributes.Directory | FileAttributes.Archive);
            File.SetAttributes(Path.Combine(sourceRoot, "switch", "prueba.nro"), FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive);
            var sourceBytes = await File.ReadAllBytesAsync(Path.Combine(sourceRoot, "switch", "prueba.nro"));
            var service = new BackupService(fake);

            var manifest = await service.CreateAsync(source, backup, Quiet, CancellationToken.None);
            Check(File.Exists(Path.Combine(backup, "manifest.json")), "Manifiesto terminado");
            Check(!File.Exists(Path.Combine(backup, "COPIA-INCOMPLETA.txt")), "Marca incompleta retirada");
            Check(manifest.Files.Count(f => !f.IsDirectory) == 5, "Todos los archivos incluidos");
            Check(manifest.ExcludedPaths.SequenceEqual(new[] { "$RECYCLE.BIN" }), "Exclusión de Windows registrada");
            Check(manifest.Files.Single(f => f.Path == "Nintendo/Contents/split.nca").Attributes.HasFlagValue(FileAttributes.Archive), "Archive de directorio conservado");
            Check(sourceBytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(sourceRoot, "switch", "prueba.nro"))), "Origen intacto");
            cases += 6;

            await ExpectThrowsAsync<IOException>(() => service.CreateAsync(source, backup, Quiet, CancellationToken.None));
            cases++;
            await service.LoadAndVerifyAsync(backup, Quiet, CancellationToken.None);
            var plan = await service.PlanAsync(backup, target, Quiet, CancellationToken.None);
            Check(plan.DataPartition.Length > data.Length, "Espacio ampliado");
            Check(fake.OpenTargetCount == 0, "Analizar no escribe destino");
            cases += 3;

            BackupService.ValidateManifest(manifest);
            ExpectThrows<InvalidDataException>(() => BackupService.ValidateManifest(manifest with { Files = manifest.Files.Concat([manifest.Files[0]]).ToArray() }));
            ExpectThrows<InvalidDataException>(() => BackupService.ValidateManifest(manifest with { Files = [manifest.Files.First(f => !f.IsDirectory) with { Path = "../escape" }] }));
            ExpectThrows<InvalidDataException>(() => BackupService.ValidateManifest(manifest with { Files = [manifest.Files.First(f => !f.IsDirectory) with { Path = "absent/child" }] }));
            ExpectThrows<InvalidDataException>(() => BackupService.ValidateManifest(manifest with { ExcludedPaths = ["Nintendo"] }));
            ExpectThrows<InvalidOperationException>(() => BackupService.ValidateDestination(manifest, source with { Number = 99 }));
            ExpectThrows<InvalidOperationException>(() => BackupService.ValidateDestination(manifest, target with { Size = source.Size - 512 }));
            ExpectThrows<InvalidOperationException>(() => BackupService.ValidateDestination(manifest, target with { IsSystem = true }));
            cases += 8;

            var badBackup = Path.Combine(backup, "files", "empty.bin");
            await File.WriteAllTextAsync(badBackup, "tampered");
            await ExpectThrowsAsync<InvalidDataException>(() => service.RestoreAsync(backup, target, Quiet, CancellationToken.None));
            Check(fake.OpenTargetCount == 0, "Copia corrupta bloqueada antes de escribir");
            await File.WriteAllBytesAsync(badBackup, []);
            cases += 2;

            var rawBackup = Path.Combine(backup, "raw", "partition-2.img");
            using (var raw = new FileStream(rawBackup, FileMode.Open, FileAccess.Write)) raw.WriteByte(0xAA);
            await ExpectThrowsAsync<InvalidDataException>(() => service.RestoreAsync(backup, target, Quiet, CancellationToken.None));
            Check(fake.OpenTargetCount == 0, "Imagen emuMMC corrupta bloqueada antes de escribir");
            using (var raw = new FileStream(rawBackup, FileMode.Open, FileAccess.Write)) raw.WriteByte(0);
            cases += 2;

            await service.RestoreAsync(backup, target, Quiet, CancellationToken.None);
            Check(fake.OpenTargetCount == 1, "Una única sesión de destino");
            Check(sourceBytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(targetRoot, "switch", "prueba.nro"))), "Copia de archivos exacta");
            Check((File.GetAttributes(Path.Combine(targetRoot, "switch", "prueba.nro")) & (FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive)) == (FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive), "Atributos de archivo exactos");
            Check((File.GetAttributes(Path.Combine(targetRoot, "Nintendo", "Contents", "split.nca")) & FileAttributes.Archive) != 0, "Archive de split-file restaurado");
            Check((await File.ReadAllTextAsync(Path.Combine(backup, "ultima-restauracion.json"))).Contains("completada-y-verificada"), "Informe de finalización");
            cases += 5;
            var restoredLayout = DiskLayout.Inspect(fake.TargetStream, target);
            Check(restoredLayout.Raw.Count == 1 && restoredLayout.Raw[0].EmuSector == plan.SectorMap[emuSector], "NAND y GPT completas tras reubicar");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(await File.ReadAllBytesAsync(Path.Combine(targetRoot, "emuMMC", "RAW1", "raw_based"))) == plan.SectorMap[emuSector], "Marcador reubicado");
            var restoredIni = await File.ReadAllTextAsync(Path.Combine(targetRoot, "emuMMC", "emummc.ini"));
            Check(restoredIni.Contains($"sector=0x{plan.SectorMap[emuSector]:X}") && restoredIni.Contains("id=0x1234\n"), "INI reubicado sin cambiar otras opciones");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(await File.ReadAllBytesAsync(Path.Combine(sourceRoot, "emuMMC", "RAW1", "raw_based"))) == emuSector, "Referencia original intacta");
            cases += 4;

            // Cancel while copying a file. No complete manifest and no target session may result.
            var cancelledBackup = Path.Combine(root, "cancelled");
            using var cts = new CancellationTokenSource();
            var cancelling = new ImmediateProgress(p => { if (p.Stage == "Creando copia") cts.Cancel(); });
            await ExpectThrowsAsync<OperationCanceledException>(() => service.CreateAsync(source, cancelledBackup, cancelling, cts.Token));
            Check(!File.Exists(Path.Combine(cancelledBackup, "manifest.json")), "Cancelación nunca completa el manifiesto");
            cases += 2;

            // A path traversal injected into JSON is rejected before opening any disk for writes.
            var originalJson = await File.ReadAllTextAsync(Path.Combine(backup, "manifest.json"));
            var malformed = manifest with { Files = [manifest.Files.First(f => !f.IsDirectory) with { Path = "../outside.txt" }] };
            await File.WriteAllTextAsync(Path.Combine(backup, "manifest.json"), JsonSerializer.Serialize(malformed));
            await ExpectThrowsAsync<InvalidDataException>(() => service.LoadAndVerifyAsync(backup, Quiet, CancellationToken.None));
            await File.WriteAllTextAsync(Path.Combine(backup, "manifest.json"), originalJson);
            cases++;

            var flushRoot = Path.Combine(root, "flush-error-target");
            Directory.CreateDirectory(flushRoot);
            var flushFailure = new FakeDisks(source, target, flushRoot) { FailFinalFlush = true };
            await ExpectThrowsAsync<IOException>(() => new BackupService(flushFailure).RestoreAsync(backup, target, Quiet, CancellationToken.None));
            var flushStatus = await File.ReadAllTextAsync(Path.Combine(backup, "ultima-restauracion.json"));
            Check(flushStatus.Contains("error-destino-incompleto") && !flushStatus.Contains("completada-y-verificada"), "Un fallo del volcado final nunca informa de éxito");
            cases += 2;
        }
        finally
        {
            // Every path is contained in our GUID-named test directory, and only that directory is removed.
            var canonical = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (!canonical.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(canonical).StartsWith("ninmagic-nx-backup-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Ruta de limpieza de pruebas no segura.");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) File.SetAttributes(dir, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        return cases;
    }

    private static bool HasFlagValue(this int attrs, FileAttributes flag) => ((FileAttributes)attrs & flag) != 0;
    private static void WriteSyntheticNand(Stream stream, uint emuSector)
    {
        var entries = new byte[128 * 128];
        entries[0] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(32), 34);
        BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(40), 4062);
        var gpp = (long)emuSector + 0x4000;
        void Put(long sector, byte[] bytes) { stream.Position = sector * 512; stream.Write(bytes); }
        Put(gpp + 1, Header(1, 4095, 2, entries));
        Put(gpp + 2, entries);
        Put(gpp + 4063, entries);
        Put(gpp + 4095, Header(4095, 1, 4063, entries));
        stream.Position = (long)emuSector * 512;
        stream.Write("SYNTHETIC BOOT0 TEST CONTENT"u8);
    }
    private static byte[] Header(ulong current, ulong alternate, ulong entriesAt, byte[] entries)
    {
        var bytes = new byte[512];
        "EFI PART"u8.CopyTo(bytes);
        void U32(int pos, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(pos), value);
        void U64(int pos, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(pos), value);
        U32(8, 0x10000); U32(12, 92); U64(24, current); U64(32, alternate);
        U64(40, 34); U64(48, 4062); U64(72, entriesAt); U32(80, 128); U32(84, 128);
        U32(88, Crc(entries)); U32(16, Crc(bytes.AsSpan(0, 92)));
        return bytes;
    }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) { crc ^= value; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); }
        return ~crc;
    }
    private static void Check(bool value, string description) { if (!value) throw new Exception("Backup test failed: " + description); }
    private static void ExpectThrows<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task ExpectThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class ImmediateProgress(Action<WorkProgress> action) : IProgress<WorkProgress>
    { public void Report(WorkProgress value) => action(value); }

    private sealed class FakeDisks(DiskInfo source, DiskInfo target, string targetRoot) : IDiskAccess
    {
        public SparseStream SourceStream { get; } = new(source.Size);
        public SparseStream TargetStream { get; } = new(target.Size);
        public int OpenTargetCount { get; private set; }
        public bool FailFinalFlush { get; init; }
        public Task<IReadOnlyList<DiskInfo>> ListDisksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DiskInfo>>([source, target]);
        public Task<DiskInfo> RefreshAsync(DiskInfo expected, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(expected); }
        public Stream OpenRead(DiskInfo disk) => SourceStream;
        public Task EnsureFolderOnOtherDiskAsync(string folder, DiskInfo disk, CancellationToken ct) => Task.CompletedTask;
        public Task<ITargetSession> OpenTargetAsync(DiskInfo disk, CancellationToken ct)
        { OpenTargetCount++; return Task.FromResult<ITargetSession>(new FakeSession(TargetStream, targetRoot, FailFinalFlush)); }
    }
    private sealed class FakeSession(SparseStream stream, string root, bool failFinalFlush) : ITargetSession
    {
        public Stream Stream => stream;
        public Task<string> MountDataAsync(MigrationPlan plan, CancellationToken ct) => Task.FromResult(root);
        public void Dispose() { if (failFinalFlush) throw new IOException("Simulated final flush failure"); }
    }
    /// <summary>Memory-backed sparse disk. No PhysicalDrive or volume APIs are called by these tests.</summary>
    private sealed class SparseStream(long length) : Stream
    {
        private readonly Dictionary<long, byte[]> pages = [];
        private const int PageSize = 4096;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var take = (int)Math.Min(buffer.Length, length - Position);
            if (take < 0) throw new IOException();
            buffer[..take].Clear();
            var done = 0;
            while (done < take)
            {
                var index = Position / PageSize;
                var within = (int)(Position % PageSize);
                var piece = Math.Min(take - done, PageSize - within);
                if (pages.TryGetValue(index, out var page)) page.AsSpan(within, piece).CopyTo(buffer.Slice(done, piece));
                Position += piece; done += piece;
            }
            return take;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position < 0 || Position + buffer.Length > length) throw new IOException("Out-of-bounds simulated write");
            var done = 0;
            while (done < buffer.Length)
            {
                var index = Position / PageSize;
                var within = (int)(Position % PageSize);
                var piece = Math.Min(buffer.Length - done, PageSize - within);
                if (!pages.TryGetValue(index, out var page)) pages[index] = page = new byte[PageSize];
                buffer.Slice(done, piece).CopyTo(page.AsSpan(within, piece));
                Position += piece; done += piece;
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => length + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
        protected override void Dispose(bool disposing) { /* shared fake survives service scopes */ }
    }
}
