using System.Security.Cryptography;
using System.Text.Json;
using Ninmagic.Nx.Core;

namespace Ninmagic.Nx.Tests;

/// <summary>Every device is an in-memory tracing stream; mounted files live in a unique test directory.</summary>
public static class TargetPolicyTests
{
    private static readonly IProgress<WorkProgress> Quiet = new ImmediateProgress(_ => { });
    private const long GiB = 1024L * 1024 * 1024;

    public static async Task<int> RunAsync()
    {
        var cases = 0;
        var source = new DiskInfo(90, "Synthetic source", "SOURCE", "SOURCE-ID", 8 * GiB,
            512, "USB", false, false, false, "MBR", []);
        var target = source with
        {
            Number = 91, SerialNumber = "TARGET", UniqueId = "TARGET-ID", Size = 256 * GiB, PartitionStyle = "GPT",
            Volumes = [new(1, 1024 * 1024, GiB, "", "exFAT", ""), new(2, 2 * GiB, GiB, "", "nTfS", "")]
        };
        var payload = "Synthetic file to preserve while converting NTFS to FAT32."u8.ToArray();
        var file = new FileEntry("sample.bin", payload.Length, Convert.ToHexString(SHA256.HashData(payload)),
            (int)FileAttributes.Normal, DateTime.UtcNow, false);
        var manifest = new BackupManifest(1, "1.0.0", DateTime.UtcNow, source,
            new(1, 0x07, 32768, (uint)(source.Size / 512 - 32768 - 2048)), "exFAT", "TEST", [],
            [file, file with { Path = "z-last.bin" }], []);
        var converted = DiskLayout.Plan(manifest, target);
        Check(converted.FileSystem == "FAT32" && converted.DataPartition.Type == 0x0C,
            "Any NTFS volume forces FAT32, including GPT and mixed case");
        Check(manifest.FileSystem == "exFAT" && manifest.DataPartition.Type == 0x07, "Source manifest is immutable");
        var otherTarget = target with { Volumes = [] };
        Check(DiskLayout.Plan(manifest, otherTarget).FileSystem == "exFAT", "Non-NTFS destination preserves exFAT");
        var fatManifest = manifest with { FileSystem = "FAT32", DataPartition = manifest.DataPartition with { Type = 0x0C } };
        Check(DiskLayout.Plan(fatManifest, otherTarget).FileSystem == "FAT32", "Non-NTFS destination preserves FAT32");
        cases += 4;

        var oversized = manifest with { Files = [file with { Length = (long)uint.MaxValue + 1 }] };
        var oversizeError = ExpectThrows<InvalidDataException>(() => DiskLayout.Plan(oversized, target));
        Check(oversizeError.Message.Contains("sample.bin") && oversizeError.Message.Contains("FAT32") &&
            oversizeError.Message.Contains("No se puede conservar"), "Oversized files have an explicit preservation error before writes");
        Check(DiskLayout.Plan(oversized, otherTarget).FileSystem == "exFAT", "Oversized exFAT file remains valid with non-NTFS target");
        Check(DiskLayout.Plan(manifest with { Files = [file with { Length = uint.MaxValue }] }, target).FileSystem == "FAT32",
            "Exact FAT32 maximum file length is accepted");
        cases += 3;

        var root = Path.Combine(Path.GetTempPath(), "ninmagic-nx-policy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var backup = Path.Combine(root, "backup");
            Directory.CreateDirectory(Path.Combine(backup, "files"));
            foreach (var entry in manifest.Files)
                await File.WriteAllBytesAsync(Path.Combine(backup, "files", entry.Path), payload);
            await File.WriteAllTextAsync(Path.Combine(backup, "manifest.json"), JsonSerializer.Serialize(manifest));

            var planned = new FakeDisks(target, Path.Combine(root, "planned-target"));
            var plan = await new BackupService(planned).PlanAsync(backup, target, Quiet, CancellationToken.None);
            Check(plan.FileSystem == "FAT32", "Planning resolves NTFS conversion");
            Check(planned.OpenCount == 0 && planned.TargetStream.BytesWritten == 0 && planned.TargetStream.BytesRead == 0,
                "Planning never opens or tests the destination");
            cases += 2;

            var corrupt = new FakeDisks(target, Path.Combine(root, "corrupt-target"));
            var tampered = payload.ToArray();
            tampered[0] ^= 0xff;
            await File.WriteAllBytesAsync(Path.Combine(backup, "files", "sample.bin"), tampered);
            await ExpectThrowsAsync<InvalidDataException>(() => new BackupService(corrupt).RestoreAsync(backup, target, Quiet, CancellationToken.None));
            Check(corrupt.OpenCount == 0 && corrupt.TargetStream.BytesWritten == 0,
                "Same-length SHA corruption is rejected before opening the destination");
            await File.WriteAllBytesAsync(Path.Combine(backup, "files", "sample.bin"), payload);
            cases += 2;

            var success = new FakeDisks(target, Path.Combine(root, "success-target"));
            var stages = new List<WorkProgress>();
            await new BackupService(success).RestoreAsync(backup, target, new ImmediateProgress(stages.Add), CancellationToken.None);
            Check(success.OpenCount == 1 && success.MountCount == 1 && success.Disposed && success.MountedPlan?.FileSystem == "FAT32",
                "One target session converts NTFS to FAT32 and closes before success");
            Check(success.TargetStream.Prefix[510] == 0x55 && success.TargetStream.Prefix[511] == 0xAA && success.TargetStream.Prefix[450] == 0x0C,
                "The destination receives a FAT32 MBR");
            Check(success.TargetStream.BytesWritten > 0 && success.TargetStream.BytesWritten < 128L * 1024 * 1024 && success.TargetStream.BytesRead == 0,
                "File-only restore writes filesystem metadata without walking or rereading all 256 GiB");
            foreach (var entry in manifest.Files)
                Check((await File.ReadAllBytesAsync(Path.Combine(success.TargetRoot, entry.Path))).SequenceEqual(payload),
                    "Every exFAT source file is preserved on the FAT32 destination");
            Check(stages.Count(p => p.Stage == "Verificando archivos") >= manifest.Files.Count,
                "Skipping the capacity scan must keep verification of the copied files");
            Check(await StateIs(backup, "completada-y-verificada") && stages.Any(p => p.Stage == "Migración verificada"),
                "Success is recorded only after verified restoration and target disposal");
            Check(!File.Exists(Path.Combine(backup, "ultima-prueba-tarjeta.json")),
                "Restoration must not claim a complete capacity test was performed");
            cases += 8;

            var writeFailure = new FakeDisks(target, Path.Combine(root, "write-error-target"));
            writeFailure.TargetStream.FailWrites = true;
            var failureStages = new List<WorkProgress>();
            await ExpectThrowsAsync<IOException>(() => new BackupService(writeFailure).RestoreAsync(backup, target,
                new ImmediateProgress(failureStages.Add), CancellationToken.None));
            Check(writeFailure.OpenCount == 1 && writeFailure.MountCount == 0 && writeFailure.Disposed,
                "A disk write failure stops before mounting and still closes the target session");
            Check(await StateIs(backup, "error-destino-incompleto") && failureStages.All(p => p.Stage != "Migración verificada"),
                "A write error replaces prior success with incomplete destination status");
            cases += 3;

            var cancelled = new FakeDisks(target, Path.Combine(root, "cancelled-target"));
            using var cts = new CancellationTokenSource();
            var cancelling = new ImmediateProgress(p => { if (p.Stage == "Preparando destino") cts.Cancel(); });
            await ExpectThrowsAsync<OperationCanceledException>(() => new BackupService(cancelled).RestoreAsync(backup, target, cancelling, cts.Token));
            Check(cancelled.TargetStream.BytesWritten == 0 && cancelled.MountCount == 0 && cancelled.Disposed,
                "Cancellation at destination preparation stops all disk writes");
            Check(await StateIs(backup, "cancelada-destino-incompleto"), "Cancellation cannot be recorded as success");
            cases += 3;

            var badCopy = new FakeDisks(target, Path.Combine(root, "bad-copy-target"));
            var changedCopy = false;
            var verificationStages = new List<WorkProgress>();
            var corrupting = new ImmediateProgress(p =>
            {
                verificationStages.Add(p);
                // Both files have been copied. While checking the first file, corrupt the second
                // before it is opened for verification. Equal length ensures SHA, not size, catches it.
                if (!changedCopy && p.Stage == "Verificando archivos" && p.Detail == "sample.bin")
                {
                    File.WriteAllBytes(Path.Combine(badCopy.TargetRoot, "z-last.bin"), tampered);
                    changedCopy = true;
                }
            });
            await ExpectThrowsAsync<IOException>(() => new BackupService(badCopy).RestoreAsync(backup, target, corrupting, CancellationToken.None));
            Check(changedCopy && badCopy.MountCount == 1 && badCopy.Disposed,
                "The destination's copied file is reread and corruption is rejected");
            Check(await StateIs(backup, "error-destino-incompleto") && verificationStages.All(p => p.Stage != "Migración verificada"),
                "A failed copied-file hash must not emit success");
            cases += 3;
        }
        finally
        {
            var canonical = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (!canonical.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(canonical).StartsWith("ninmagic-nx-policy-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup path");
            Directory.Delete(canonical, true);
        }
        return cases;
    }

    private static async Task<bool> StateIs(string backup, string state)
    {
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "ultima-restauracion.json")));
        return report.RootElement.GetProperty("state").GetString() == state;
    }

    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception("Target policy: " + description); }
    private static T ExpectThrows<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task ExpectThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class ImmediateProgress(Action<WorkProgress> action) : IProgress<WorkProgress>
    { public void Report(WorkProgress value) => action(value); }

    private sealed class FakeDisks(DiskInfo target, string targetRoot) : IDiskAccess, ITargetSession
    {
        public TracingStream TargetStream { get; } = new(target.Size);
        public string TargetRoot => targetRoot;
        public int OpenCount { get; private set; }
        public int MountCount { get; private set; }
        public bool Disposed { get; private set; }
        public MigrationPlan? MountedPlan { get; private set; }
        public Stream Stream => TargetStream;
        public Task<IReadOnlyList<DiskInfo>> ListDisksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DiskInfo>>([target]);
        public Task<DiskInfo> RefreshAsync(DiskInfo expected, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(target); }
        public Stream OpenRead(DiskInfo disk) => throw new InvalidOperationException("No source device access permitted by this suite");
        public Task EnsureFolderOnOtherDiskAsync(string folder, DiskInfo disk, CancellationToken ct) => Task.CompletedTask;
        public Task<ITargetSession> OpenTargetAsync(DiskInfo disk, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); OpenCount++; return Task.FromResult<ITargetSession>(this); }
        public Task<string> MountDataAsync(MigrationPlan plan, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            MountCount++; MountedPlan = plan;
            Directory.CreateDirectory(targetRoot);
            return Task.FromResult(targetRoot);
        }
        public void Dispose() { Disposed = true; }
    }

    private sealed class TracingStream(long length) : Stream
    {
        // Fail quickly if a future regression starts traversing the full advertised device.
        // A 256 GiB FAT32 quick format needs less than 128 MiB for its metadata and GPT cleanup.
        private const long IoBudget = 128L * 1024 * 1024;
        public byte[] Prefix { get; } = new byte[512];
        public long BytesWritten { get; private set; }
        public long BytesRead { get; private set; }
        public bool FailWrites { get; set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (Position < 0 || Position > length) throw new IOException("Out-of-bounds simulated read");
            int count = (int)Math.Min(buffer.Length, length - Position);
            if (BytesRead + count > IoBudget) throw new IOException("Unexpected full-capacity reading exceeded the simulated I/O budget");
            buffer[..count].Clear();
            BytesRead += count; Position += count;
            return count;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (FailWrites) throw new IOException("Simulated destination write error");
            if (Position < 0 || buffer.Length > length - Position) throw new IOException("Out-of-bounds simulated write");
            if (BytesWritten + buffer.Length > IoBudget) throw new IOException("Unexpected full-capacity writing exceeded the simulated I/O budget");
            BytesWritten += buffer.Length;
            if (Position < Prefix.Length) buffer[..(int)Math.Min(buffer.Length, Prefix.Length - Position)].CopyTo(Prefix.AsSpan((int)Position));
            Position += buffer.Length;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => length + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
