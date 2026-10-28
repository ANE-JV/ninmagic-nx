namespace Ninmagic.Nx.Core;

public sealed record DiskInfo(int Number, string FriendlyName, string SerialNumber, string UniqueId,
    long Size, int LogicalSectorSize, string BusType, bool IsBoot, bool IsSystem, bool IsReadOnly,
    string PartitionStyle, IReadOnlyList<VolumeInfo> Volumes)
{
    public string Identity => $"{Number}|{SerialNumber}|{UniqueId}|{Size}|{LogicalSectorSize}|{BusType}";
    public override string ToString() => $"Disco {Number} · {FriendlyName} · {Size / 1_000_000_000.0:0.0} GB · {string.Join(", ", Volumes.Where(v => !string.IsNullOrWhiteSpace(v.FileSystem)).Select(v => v.Root.Length <= 3 ? v.Root : v.FileSystem))}";
}
public sealed record VolumeInfo(int PartitionNumber, long Offset, long Size, string Root, string FileSystem, string Label);
public sealed record PartitionInfo(int Index, byte Type, uint StartSector, uint SectorCount)
{
    public long Offset => (long)StartSector * 512;
    public long Length => (long)SectorCount * 512;
}
public sealed record RawPartition(PartitionInfo Partition, uint EmuSector, string ImageFile, string Sha256);
public sealed record FileEntry(string Path, long Length, string Sha256, int Attributes, DateTime LastWriteTimeUtc, bool IsDirectory);
public sealed record BackupManifest(int FormatVersion, string AppVersion, DateTime CreatedUtc,
    DiskInfo Source, PartitionInfo DataPartition, string FileSystem, string VolumeLabel,
    IReadOnlyList<RawPartition> RawPartitions, IReadOnlyList<FileEntry> Files, IReadOnlyList<string> ExcludedPaths);
public sealed record MigrationPlan(PartitionInfo DataPartition, IReadOnlyList<PartitionInfo> RawPartitions,
    IReadOnlyDictionary<uint, uint> SectorMap, long RequiredFileBytes, string FileSystem, long TargetSize);
public sealed record WorkProgress(string Stage, string Detail, long Completed, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Completed / Total, 0, 1) : 0;
}
public interface IDiskAccess
{
    Task<IReadOnlyList<DiskInfo>> ListDisksAsync(CancellationToken cancellationToken);
    Task<DiskInfo> RefreshAsync(DiskInfo expected, CancellationToken cancellationToken);
    Stream OpenRead(DiskInfo disk);
    Task EnsureFolderOnOtherDiskAsync(string folder, DiskInfo disk, CancellationToken cancellationToken);
    Task<ITargetSession> OpenTargetAsync(DiskInfo disk, CancellationToken cancellationToken);
}
public interface ITargetSession : IDisposable
{
    Stream Stream { get; }
    Task<string> MountDataAsync(MigrationPlan plan, CancellationToken cancellationToken);
}
