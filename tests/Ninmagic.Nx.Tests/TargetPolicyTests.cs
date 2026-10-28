using System.Security.Cryptography;
using System.Text.Json;
using Ninmagic.Nx.Core;

namespace Ninmagic.Nx.Tests;

/// <summary>Orchestration tests only. Every target is an in-memory, write-tracing simulated disk.</summary>
public static class TargetPolicyTests
{
    private static readonly IProgress<WorkProgress> Quiet = new ImmediateProgress();
    private const long GiB = 1024L * 1024 * 1024;

    public static async Task<int> RunAsync()
    {
        var cases = 0;
        var source = new DiskInfo(90, "Synthetic source", "SOURCE", "SOURCE-ID", 8 * GiB,
            512, "USB", false, false, false, "MBR", []);
        var target = source with
        {
            Number = 91, SerialNumber = "TARGET", UniqueId = "TARGET-ID", Size = 16 * GiB, PartitionStyle = "GPT",
            Volumes = [new(1, 1024 * 1024, GiB, "", "exFAT", ""), new(2, 2 * GiB, GiB, "", "nTfS", "")]
        };
        var payload = "Synthetic file to preserve while converting NTFS to FAT32."u8.ToArray();
        var file = new FileEntry("sample.bin", payload.Length, Convert.ToHexString(SHA256.HashData(payload)),
            (int)FileAttributes.Normal, DateTime.UtcNow, false);
        var manifest = new BackupManifest(1, "1.0.0", DateTime.UtcNow, source,
            new(1, 0x07, 32768, (uint)(source.Size / 512 - 32768 - 2048)), "exFAT", "TEST", [], [file], []);
        var converted = DiskLayout.Plan(manifest, target);
        Check(converted.FileSystem == "FAT32" && converted.DataPartition.Type == 0x0C, "Any NTFS volume forces FAT32, including GPT and mixed case");
        Check(manifest.FileSystem == "exFAT" && manifest.DataPartition.Type == 0x07, "Source manifest is immutable");
        var otherTarget = target with { Volumes = [] };
        Check(DiskLayout.Plan(manifest, otherTarget).FileSystem == "exFAT", "Non-NTFS destination preserves exFAT");
        var fatManifest = manifest with { FileSystem = "FAT32", DataPartition = manifest.DataPartition with { Type = 0x0C } };
        Check(DiskLayout.Plan(fatManifest, otherTarget).FileSystem == "FAT32", "Non-NTFS destination preserves FAT32");
        cases += 4;

        var oversized = manifest with { Files = [file with { Length = (long)uint.MaxValue + 1 }] };
        var oversizeError = ExpectThrows<InvalidDataException>(() => DiskLayout.Plan(oversized, target));
        Check(oversizeError.Message.Contains("sample.bin") && oversizeError.Message.Contains("FAT32") && oversizeError.Message.Contains("No se puede conservar"), "Oversized files have explicit preservation error before writes");
        Check(DiskLayout.Plan(oversized, otherTarget).FileSystem == "exFAT", "Oversized exFAT file remains valid with non-NTFS target");
        Check(DiskLayout.Plan(manifest with { Files = [file with { Length = uint.MaxValue }] }, target).FileSystem == "FAT32", "Exact FAT32 maximum file length is accepted");
        cases += 3;

        var root = Path.Combine(Path.GetTempPath(), "ninmagic-nx-policy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var backup = Path.Combine(root, "backup");
            Directory.CreateDirectory(Path.Combine(backup, "files"));
            await File.WriteAllBytesAsync(Path.Combine(backup, "files", "sample.bin"), payload);
            await File.WriteAllTextAsync(Path.Combine(backup, "manifest.json"), JsonSerializer.Serialize(manifest));

            var failed = new FakeDisks(target, Path.Combine(root, "failed-target"));
            var failedTestCalled = false;
            var failingService = new BackupService(failed, (stream, size, _, ct) =>
            {
                Check(ReferenceEquals(stream, failed.TargetStream) && failed.OpenCount == 1 && !failed.Disposed, "Media tester owns same active target session");
                Check(size == target.Size, "Media test must cover entire reported capacity");
                failedTestCalled = true;
                stream.Position = 0;
                stream.WriteByte(0x7D);
                throw new MediaTestException("lectura", 4096, "Simulated counterfeit media");
            });
            var plan = await failingService.PlanAsync(backup, target, Quiet, CancellationToken.None);
            Check(plan.FileSystem == "FAT32" && failed.OpenCount == 0 && !failedTestCalled, "Planning resolves NTFS policy without opening target");
            await ExpectThrowsAsync<IOException>(() => failingService.RestoreAsync(backup, target, Quiet, CancellationToken.None));
            Check(failedTestCalled && failed.TargetStream.WriteCalls == 1 && failed.TargetStream.Prefix[0] == 0x7D && failed.MountCount == 0 && failed.Disposed,
                "Media failure stops MBR, formatter, mounting and restoration, disposing session");
            using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "ultima-prueba-tarjeta.json"))))
                Check(report.RootElement.GetProperty("state").GetString() == "fallida" && report.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null &&
                    report.RootElement.GetProperty("phase").GetString() == "lectura" && report.RootElement.GetProperty("byteOffset").GetInt64() == 4096,
                    "Failed media report retains diagnostic phase and offset without a pass result");
            Check((await File.ReadAllTextAsync(Path.Combine(backup, "ultima-restauracion.json"))).Contains("error-destino-incompleto"), "Media failure marks incomplete destination");
            cases += 6;

            var cancelled = new FakeDisks(target, Path.Combine(root, "cancelled-target"));
            using var cts = new CancellationTokenSource();
            var cancellingService = new BackupService(cancelled, (stream, size, _, ct) =>
            {
                stream.WriteByte(0x7C);
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                throw new Exception("Unreachable");
            });
            await ExpectThrowsAsync<OperationCanceledException>(() => cancellingService.RestoreAsync(backup, target, Quiet, cts.Token));
            Check(cancelled.TargetStream.WriteCalls == 1 && cancelled.TargetStream.Prefix[0] == 0x7C && cancelled.MountCount == 0, "Cancelled media test cannot format or restore");
            using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "ultima-prueba-tarjeta.json"))))
                Check(report.RootElement.GetProperty("state").GetString() == "cancelada", "Cancelled media report retained");
            cases += 2;

            var partial = new FakeDisks(target, Path.Combine(root, "partial-target"));
            var partialService = new BackupService(partial, (_, size, _, _) => Task.FromResult(new MediaTestResult(size - 512, 1, 1, DateTime.UtcNow, "")));
            await ExpectThrowsAsync<IOException>(() => partialService.RestoreAsync(backup, target, Quiet, CancellationToken.None));
            Check(partial.TargetStream.WriteCalls == 0 && partial.MountCount == 0, "Partial capacity result is rejected before MBR/formatting");
            cases++;

            var corrupt = new FakeDisks(target, Path.Combine(root, "corrupt-target"));
            var corruptTestCalled = false;
            var corruptService = new BackupService(corrupt, (_, size, _, _) =>
            {
                corruptTestCalled = true;
                return Task.FromResult(new MediaTestResult(size, 1, 1, DateTime.UtcNow, ""));
            });
            await File.WriteAllTextAsync(Path.Combine(backup, "files", "sample.bin"), "tampered");
            await ExpectThrowsAsync<InvalidDataException>(() => corruptService.RestoreAsync(backup, target, Quiet, CancellationToken.None));
            Check(corrupt.OpenCount == 0 && !corruptTestCalled, "Corrupt backup cannot trigger destructive test");
            await File.WriteAllBytesAsync(Path.Combine(backup, "files", "sample.bin"), payload);
            cases++;

            var success = new FakeDisks(target, Path.Combine(root, "success-target"));
            var expectedResult = new MediaTestResult(target.Size, 61.25, 82.5, DateTime.UtcNow, "Synthetic performance warning");
            var testFinished = false;
            success.BeforeMount = () => Check(testFinished, "Mount cannot precede completed media test");
            var service = new BackupService(success, (stream, size, _, _) =>
            {
                Check(success.TargetStream.WriteCalls == 0 && size == target.Size && ReferenceEquals(stream, success.TargetStream), "Capacity test precedes all formatting using same disk");
                testFinished = true;
                return Task.FromResult(expectedResult);
            });
            var actualResult = await service.RestoreAsync(backup, target, Quiet, CancellationToken.None);
            Check(actualResult == expectedResult, "Verified media metrics propagated to caller");
            Check(success.OpenCount == 1 && success.MountCount == 1 && success.Disposed && success.MountedPlan?.FileSystem == "FAT32", "One locked session converts to FAT32 after passing");
            Check(success.TargetStream.Prefix[510] == 0x55 && success.TargetStream.Prefix[511] == 0xAA && success.TargetStream.Prefix[450] == 0x0C, "FAT32 MBR written only after media success");
            Check((await File.ReadAllBytesAsync(Path.Combine(success.TargetRoot, "sample.bin"))).SequenceEqual(payload), "exFAT source file preserved on FAT32 destination");
            using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "ultima-prueba-tarjeta.json"))))
            {
                var record = report.RootElement;
                Check(record.GetProperty("state").GetString() == "capacidad-completa-verificada" &&
                    record.GetProperty("result").GetProperty("BytesVerified").GetInt64() == target.Size &&
                    record.GetProperty("result").GetProperty("WriteMiBPerSecond").GetDouble() == expectedResult.WriteMiBPerSecond,
                    "Separate media report records full capacity and speed");
            }
            Check((await File.ReadAllTextAsync(Path.Combine(backup, "ultima-restauracion.json"))).Contains("completada-y-verificada"), "Success recorded only after target disposal");
            Check(typeof(BackupService).GetConstructors().Length == 1 && typeof(BackupService).GetConstructors()[0].GetParameters().Length == 1,
                "Production API exposes no capacity-test bypass");
            cases += 9;
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
    private sealed class ImmediateProgress : IProgress<WorkProgress> { public void Report(WorkProgress value) { } }

    private sealed class FakeDisks(DiskInfo target, string targetRoot) : IDiskAccess, ITargetSession
    {
        public TracingStream TargetStream { get; } = new(target.Size);
        public string TargetRoot => targetRoot;
        public int OpenCount { get; private set; }
        public int MountCount { get; private set; }
        public bool Disposed { get; private set; }
        public MigrationPlan? MountedPlan { get; private set; }
        public Action? BeforeMount { get; set; }
        public Stream Stream => TargetStream;
        public Task<IReadOnlyList<DiskInfo>> ListDisksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DiskInfo>>([target]);
        public Task<DiskInfo> RefreshAsync(DiskInfo expected, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(target); }
        public Stream OpenRead(DiskInfo disk) => throw new InvalidOperationException("No source device access permitted by this suite");
        public Task EnsureFolderOnOtherDiskAsync(string folder, DiskInfo disk, CancellationToken ct) => Task.CompletedTask;
        public Task<ITargetSession> OpenTargetAsync(DiskInfo disk, CancellationToken ct) { OpenCount++; return Task.FromResult<ITargetSession>(this); }
        public Task<string> MountDataAsync(MigrationPlan plan, CancellationToken ct)
        {
            BeforeMount?.Invoke(); MountCount++; MountedPlan = plan;
            Directory.CreateDirectory(targetRoot);
            return Task.FromResult(targetRoot);
        }
        public void Dispose() { Disposed = true; }
    }

    private sealed class TracingStream(long length) : Stream
    {
        public byte[] Prefix { get; } = new byte[512];
        public int WriteCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The injected media test does not read this tracing stream");
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position < 0 || Position + buffer.Length > length) throw new IOException("Out-of-bounds simulated write");
            WriteCalls++;
            if (Position < Prefix.Length) buffer[..(int)Math.Min(buffer.Length, Prefix.Length - Position)].CopyTo(Prefix.AsSpan((int)Position));
            Position += buffer.Length;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => length + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
