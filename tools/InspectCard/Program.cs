using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ninmagic.Nx.Core;
using Ninmagic.Nx.Windows;

// This diagnostic deliberately contains no target session, formatting, patching, or restore calls.
// Its only writes are a small reference snapshot and JSON report in a validated folder on another disk.
var report = new Dictionary<string, object?>
{
    ["tool"] = "ninmagic-nx-inspect-readonly",
    ["startedUtc"] = DateTime.UtcNow,
    ["mode"] = "read-only",
    ["sourceWriteOperationsAttempted"] = 0,
    ["success"] = false
};
string? outputPath = null;
string? outputDirectory = null;
bool outputLocationValidated = false;
int result = 1;

try
{
    if (args.Length != 3 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int diskNumber) ||
        diskNumber < 0 || !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out long expectedSize) ||
        expectedSize <= 0 || expectedSize % 512 != 0)
        throw new ArgumentException("Usage: ninmagic-nx-inspect-readonly.exe DISK_NUMBER EXACT_SIZE_BYTES ABSOLUTE_REPORT_JSON_PATH");
    if (!Path.IsPathFullyQualified(args[2])) throw new ArgumentException("The report path must be absolute.");
    outputPath = Path.GetFullPath(args[2]);
    if (!outputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("The report filename must end with .json.");
    outputDirectory = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException("The report folder is missing.");
    SafePaths.RejectReparseAncestors(outputPath);
    if (File.Exists(outputPath)) throw new IOException("The report already exists. Use a new report filename.");
    report["requestedDiskNumber"] = diskNumber;
    report["expectedSizeBytes"] = expectedSize;

    var adapter = new WindowsDiskAccess();
    // This guard only uses the requested disk number and resolves the output folder's physical disk.
    var outputGuard = new DiskInfo(diskNumber, "Read-only output guard", "", "", expectedSize, 512,
        "USB", false, false, false, "MBR", []);
    await adapter.EnsureFolderOnOtherDiskAsync(outputDirectory, outputGuard, CancellationToken.None);
    outputLocationValidated = true;
    report["reportLocationConfirmedOnDifferentDisk"] = true;
    bool administrator = WindowsDiskAccess.IsAdministrator;
    report["administrator"] = administrator;
    if (!administrator)
        throw new UnauthorizedAccessException("Administrator access is required for read-only physical disk inspection. No physical disk was opened by this diagnostic.");

    IReadOnlyList<DiskInfo> candidates = await adapter.ListDisksAsync(CancellationToken.None);
    DiskInfo disk = candidates.SingleOrDefault(item => item.Number == diskNumber && item.Size == expectedSize)
        ?? throw new IOException("The exact requested external disk number and size were not found among safe candidates.");
    disk = await adapter.RefreshAsync(disk, CancellationToken.None);
    await adapter.EnsureFolderOnOtherDiskAsync(outputDirectory, disk, CancellationToken.None);
    report["disk"] = disk;
    report["candidateCount"] = candidates.Count;

    using Stream source = adapter.OpenRead(disk);
    if (source.CanWrite) throw new InvalidOperationException("The physical source stream unexpectedly allows writing.");
    report["physicalStreamCanWrite"] = source.CanWrite;
    byte[] firstSectorBefore = ReadFirstSector(source);
    report["mbrSha256Before"] = Digest(firstSectorBefore);
    var layout = DiskLayout.Inspect(source, disk);
    report["layout"] = new
    {
        layout.FileSystem,
        DataPartition = layout.Data,
        RawPartitions = layout.Raw.Select(raw => new
        {
            raw.Partition,
            raw.EmuSector,
            EmuSectorHex = $"0x{raw.EmuSector:X}",
            RelativeEmuSector = raw.EmuSector - raw.Partition.StartSector,
            PrimaryNandGptCrcValidated = true,
            BackupNandGptCrcValidated = true,
            NandPartitionArraysMatch = true
        }).ToArray()
    };

    VolumeInfo volume = disk.Volumes.SingleOrDefault(item => item.Offset == layout.Data.Offset && item.Size == layout.Data.Length)
        ?? throw new IOException("The files partition does not map to one Windows volume.");
    if (string.IsNullOrWhiteSpace(volume.Root) || !Directory.Exists(volume.Root))
        throw new IOException("The source files volume is not readable.");
    SafePaths.RejectReparseAncestors(volume.Root);
    if (!string.Equals(volume.FileSystem, layout.FileSystem, StringComparison.OrdinalIgnoreCase))
        throw new IOException("Windows and the raw filesystem header disagree.");

    // Only the tiny INI and four-byte markers are copied to this PC snapshot.
    // Large file-based eMMC files, if present, are inspected for metadata only.
    string snapshot = Path.Combine(outputDirectory, "inspect-reference-" + Guid.NewGuid().ToString("N"));
    SafePaths.RejectReparseAncestors(snapshot);
    Directory.CreateDirectory(Path.Combine(snapshot, "files"));
    report["pcReferenceSnapshot"] = snapshot;
    var entries = new List<FileEntry>();
    var references = new List<ReferenceRecord>();
    ReadReference("emuMMC/emummc.ini", 1024 * 1024, false);
    string emuRoot = SafePaths.Resolve(volume.Root, "emuMMC");
    if (Directory.Exists(emuRoot))
    {
        string[] directories = Directory.GetDirectories(emuRoot);
        if (directories.Length > 128) throw new InvalidDataException("More than 128 emuMMC folders; refusing unbounded reference inspection.");
        foreach (string directory in directories)
        {
            SafePaths.RejectReparseAncestors(directory);
            string relative = SafePaths.NormalizeRelative(Path.GetRelativePath(volume.Root, directory));
            ReadReference(relative + "/raw_based", 4, true);
            foreach (string name in new[] { "BOOT0", "BOOT1", "00" })
            {
                string imageRelative = relative + "/eMMC/" + name;
                string path = SafePaths.Resolve(volume.Root, imageRelative);
                if (!File.Exists(path)) continue;
                var info = new FileInfo(path);
                entries.Add(new FileEntry(imageRelative, info.Length, "", (int)info.Attributes, info.LastWriteTimeUtc, false));
            }
        }
    }
    var manifest = new BackupManifest(1, "read-only-diagnostic", DateTime.UtcNow, disk, layout.Data, layout.FileSystem,
        volume.Label, layout.Raw.Select(raw => new RawPartition(raw.Partition, raw.EmuSector, "", "")).ToArray(),
        entries, []);
    EmuMmc.ValidateReferences(manifest, snapshot);
    report["emuMmcReferencesValidated"] = true;
    report["references"] = references;

    // Re-read the inspected source bytes to detect changes during the diagnostic.
    foreach (ReferenceRecord reference in references)
    {
        byte[] current = ReadSmallFile(SafePaths.Resolve(volume.Root, reference.Path), reference.Length);
        if (Digest(current) != reference.Sha256) throw new IOException("A source reference changed while being inspected: " + reference.Path);
    }
    byte[] firstSectorAfter = ReadFirstSector(source);
    report["mbrSha256After"] = Digest(firstSectorAfter);
    if (!firstSectorAfter.AsSpan().SequenceEqual(firstSectorBefore)) throw new IOException("The source MBR changed during inspection.");
    report["inspectedReferenceBytesUnchanged"] = true;
    report["mbrUnchanged"] = true;
    report["success"] = true;
    report["scope"] = "Validated partition geometry, filesystem boot metadata, internal primary and backup NAND GPTs and emuMMC references. Did not read or verify every game, save, FAT chain or NAND sector. Did not test a migration or console boot.";
    result = 0;

    void ReadReference(string relative, int maximum, bool exactFour)
    {
        string sourcePath = SafePaths.Resolve(volume.Root, relative);
        if (!File.Exists(sourcePath)) return;
        byte[] bytes = ReadSmallFile(sourcePath, maximum);
        if (exactFour && bytes.Length != 4) throw new InvalidDataException("A raw_based marker does not have exactly four bytes.");
        var info = new FileInfo(sourcePath);
        string destination = SafePaths.Resolve(Path.Combine(snapshot, "files"), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        string digest = Digest(bytes);
        entries.Add(new FileEntry(relative, bytes.Length, digest, (int)info.Attributes, info.LastWriteTimeUtc, false));
        references.Add(new ReferenceRecord(relative, bytes.Length, digest,
            exactFour ? $"0x{BinaryPrimitives.ReadUInt32LittleEndian(bytes):X}" : null,
            exactFour ? null : DecodeIni(bytes)));
    }
}
catch (Exception error)
{
    report["success"] = false;
    report["error"] = new { Type = error.GetType().FullName, error.Message, Details = error.ToString() };
    Console.Error.WriteLine(error.Message);
}
finally
{
    report["completedUtc"] = DateTime.UtcNow;
    if (outputLocationValidated && outputPath is not null && outputDirectory is not null)
    {
        try
        {
            SafePaths.RejectReparseAncestors(outputPath);
            Directory.CreateDirectory(outputDirectory);
            using var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, report, new JsonSerializerOptions { WriteIndented = true });
            file.Flush(true);
            Console.WriteLine(outputPath);
        }
        catch (Exception reportError)
        {
            Console.Error.WriteLine("Could not write the report: " + reportError.Message);
            result = 2;
        }
    }
    else
    {
        Console.Error.WriteLine("No report file was written because its path was not confirmed on a different local disk.");
        result = 2;
    }
}
return result;

static byte[] ReadFirstSector(Stream stream)
{
    stream.Position = 0;
    byte[] bytes = new byte[512];
    stream.ReadExactly(bytes);
    return bytes;
}

static byte[] ReadSmallFile(string path, int maximum)
{
    SafePaths.RejectReparseAncestors(path);
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (stream.Length < 0 || stream.Length > maximum) throw new InvalidDataException("Reference file exceeds its diagnostic size limit: " + path);
    byte[] result = new byte[(int)stream.Length];
    stream.ReadExactly(result);
    return result;
}

static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

static string DecodeIni(byte[] bytes)
{
    using var reader = new StreamReader(new MemoryStream(bytes, false), Encoding.UTF8, true);
    return reader.ReadToEnd();
}

internal sealed record ReferenceRecord(string Path, int Length, string Sha256, string? SectorHex, string? IniText);
