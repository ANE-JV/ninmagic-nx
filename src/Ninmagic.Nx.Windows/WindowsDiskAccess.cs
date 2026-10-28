using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Ninmagic.Nx.Core;

namespace Ninmagic.Nx.Windows;

public sealed class WindowsDiskAccess : IDiskAccess
{
    private readonly ConcurrentDictionary<string, string> selectionFingerprints = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string InventoryScript = """
        $items = @(Get-Disk | Where-Object { -not $_.IsOffline } | ForEach-Object {
            $d = $_
            $volumes = @(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue | ForEach-Object {
                $part = $_
                $v = $part | Get-Volume -ErrorAction SilentlyContinue
                $root = ''
                if ($v -and $v.Path) { $root = [string]$v.Path }
                elseif ($part.DriveLetter) { $root = [string]$part.DriveLetter + ':\' }
                [pscustomobject]@{
                    PartitionNumber=[int]$part.PartitionNumber; Offset=[long]$part.Offset; Size=[long]$part.Size;
                    Root=$root; FileSystem=[string]$v.FileSystemType; Label=[string]$v.FileSystemLabel
                }
            })
            [pscustomobject]@{
                Number=[int]$d.Number; FriendlyName=[string]$d.FriendlyName;
                SerialNumber=([string]$d.SerialNumber).Trim(); UniqueId=([string]$d.UniqueId).Trim();
                Size=[long]$d.Size; LogicalSectorSize=[int]$d.LogicalSectorSize; BusType=[string]$d.BusType;
                IsBoot=[bool]$d.IsBoot; IsSystem=[bool]$d.IsSystem; IsReadOnly=[bool]$d.IsReadOnly;
                PartitionStyle=[string]$d.PartitionStyle; Volumes=$volumes
            }
        })
        ConvertTo-Json -InputObject $items -Depth 5 -Compress
        """;

    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    public async Task<IReadOnlyList<DiskInfo>> ListDisksAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DiskInfo> inventory = await ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
        var allowed = inventory.Where(IsEligible).ToArray();
        var duplicateIds = allowed.GroupBy(HardwareKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var result = allowed.Where(disk => !duplicateIds.Contains(HardwareKey(disk))).ToArray();
        if (IsAdministrator)
        {
            foreach (DiskInfo disk in result)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { selectionFingerprints[disk.Identity] = NativeDisk.Fingerprint(disk.Number, disk.Size); }
                catch (IOException) { selectionFingerprints.TryRemove(disk.Identity, out _); }
                catch (System.ComponentModel.Win32Exception) { selectionFingerprints.TryRemove(disk.Identity, out _); }
            }
        }
        return result;
    }

    private static string HardwareKey(DiskInfo disk) => $"{disk.BusType}|{disk.SerialNumber}|{disk.UniqueId}";

    public static bool IsEligible(DiskInfo disk) => disk.Number is >= 0 and <= 65535 && disk.Size >= 32L * 1024 * 1024 && disk.Size % 512 == 0 &&
        disk.Size / 512 <= uint.MaxValue && disk.LogicalSectorSize == 512 && !disk.IsBoot && !disk.IsSystem &&
        disk.BusType is "USB" or "SD" or "MMC" &&
        (!string.IsNullOrWhiteSpace(disk.SerialNumber) || !string.IsNullOrWhiteSpace(disk.UniqueId));

    private static async Task<IReadOnlyList<DiskInfo>> ReadInventoryAsync(CancellationToken cancellationToken)
    {
        string json = await PowerShellRunner.RunAsync(InventoryScript, null, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<DiskInfo[]>(json, JsonOptions) ?? [];
    }

    public async Task<DiskInfo> RefreshAsync(DiskInfo expected, CancellationToken cancellationToken)
    {
        IReadOnlyList<DiskInfo> all = await ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
        DiskInfo? actual = all.SingleOrDefault(disk => disk.Number == expected.Number);
        if (actual is null || !IsEligible(actual) || !string.Equals(expected.Identity, actual.Identity, StringComparison.Ordinal) ||
            all.Count(disk => HardwareKey(disk) == HardwareKey(actual)) != 1)
            throw new IOException("La tarjeta seleccionada ya no coincide. Vuelve a buscar y seleccionar las tarjetas.");
        return actual;
    }

    public Stream OpenRead(DiskInfo disk)
    {
        RequireAdministrator();
        if (!IsEligible(disk)) throw new IOException("Este disco no cumple las condiciones de seguridad.");
        if (!selectionFingerprints.TryGetValue(disk.Identity, out string? expectedFingerprint))
            throw new IOException("Vuelve a buscar las tarjetas como administrador antes de crear la copia.");
        // The calling workflow refreshes metadata first. Raw open also verifies length and device number.
        SafeFileHandle handle = NativeDisk.OpenPhysical(disk.Number, false, disk.Size);
        try
        {
            var stream = new PhysicalDiskStream(handle, disk.Size, false);
            try
            {
                CheckOpenedFingerprint(stream, expectedFingerprint);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        catch { handle.Dispose(); throw; }
    }

    private static void CheckOpenedFingerprint(Stream stream, string expected)
    {
        byte[] sector = new byte[512];
        stream.Position = 0;
        stream.ReadExactly(sector);
        stream.Position = 0;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(sector)), expected, StringComparison.Ordinal))
            throw new IOException("La tarjeta ha cambiado desde su selección. Vuelve a buscar y seleccionar las tarjetas.");
    }

    public async Task EnsureFolderOnOtherDiskAsync(string folder, DiskInfo disk, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(folder);
        if (fullPath.Length < 3 || !char.IsAsciiLetter(fullPath[0]) || fullPath[1] != ':' || fullPath[2] != '\\')
            throw new IOException("Guarda la copia en una carpeta local del PC con letra de unidad; no se admiten rutas de red.");
        string? current = fullPath;
        while (current is not null)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("La carpeta de copia no puede atravesar enlaces, uniones ni carpetas redirigidas.");
            }
            current = Path.GetDirectoryName(current);
        }
        const string script = """
            $parts = @(Get-Partition -DriveLetter $p.Letter -ErrorAction Stop)
            if ($parts.Count -ne 1) { throw 'No se puede identificar el disco de la carpeta de copia.' }
            [pscustomobject]@{ DiskNumber=[int]$parts[0].DiskNumber } | ConvertTo-Json -Compress
            """;
        string json = await PowerShellRunner.RunAsync(script, new { Letter = fullPath[..1] }, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("DiskNumber").GetInt32() == disk.Number)
            throw new IOException("La copia de seguridad debe estar en otro disco, nunca en la tarjeta que se está leyendo o borrando.");
    }

    public async Task<ITargetSession> OpenTargetAsync(DiskInfo disk, CancellationToken cancellationToken)
    {
        RequireAdministrator();
        DiskInfo fresh = await RefreshAsync(disk, cancellationToken).ConfigureAwait(false);
        if (fresh.IsReadOnly) throw new IOException("La tarjeta de destino está protegida contra escritura.");
        if (!selectionFingerprints.TryGetValue(disk.Identity, out string? selectedFingerprint))
            throw new IOException("Vuelve a buscar las tarjetas como administrador antes de restaurar.");
        if (NativeDisk.Fingerprint(fresh.Number, fresh.Size) != selectedFingerprint)
            throw new IOException("La estructura de la tarjeta ha cambiado desde su selección. Vuelve a seleccionarla.");
        IReadOnlyList<NativeDisk.VolumeExtent> volumes = NativeDisk.EnumerateVolumes();
        NativeDisk.VolumeExtent[] targetVolumes = volumes.Where(volume => volume.Extents.Any(extent => extent.Disk == fresh.Number)).ToArray();
        if (targetVolumes.Any(volume => volume.Extents.Count != 1))
            throw new IOException("No se admiten volúmenes dinámicos o distribuidos en varios discos.");
        foreach (VolumeInfo known in fresh.Volumes.Where(volume => !string.IsNullOrWhiteSpace(volume.Root)))
        {
            if (!targetVolumes.Any(volume => volume.Extents.Any(extent => extent.Offset == known.Offset && extent.Length == known.Size)))
                throw new IOException("No se han podido identificar todos los volúmenes del destino. No se ha escrito en la tarjeta.");
        }
        var locks = new List<SafeFileHandle>();
        try
        {
            foreach (NativeDisk.VolumeExtent volume in targetVolumes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                locks.Add(NativeDisk.LockAndDismount(volume.Path));
            }
            // Check again after locks and immediately before opening the writable physical device.
            fresh = await RefreshAsync(disk, cancellationToken).ConfigureAwait(false);
            if (fresh.IsReadOnly || NativeDisk.Fingerprint(fresh.Number, fresh.Size) != selectedFingerprint)
                throw new IOException("El destino ha cambiado. No se ha escrito en la tarjeta.");
            cancellationToken.ThrowIfCancellationRequested();
            SafeFileHandle physical = NativeDisk.OpenPhysical(fresh.Number, true, fresh.Size);
            try
            {
                var stream = new PhysicalDiskStream(physical, fresh.Size, true);
                try
                {
                    CheckOpenedFingerprint(stream, selectedFingerprint);
                    return new TargetSession(this, fresh, physical, stream, locks);
                }
                catch { stream.Dispose(); throw; }
            }
            catch { physical.Dispose(); throw; }
        }
        catch
        {
            foreach (SafeFileHandle handle in locks) handle.Dispose();
            throw;
        }
    }

    private static void RequireAdministrator()
    {
        if (!IsAdministrator) throw new UnauthorizedAccessException("Abre ninmagic-nx como administrador para acceder a las tarjetas.");
    }

    internal static void CloseRawResources(Stream stream, List<SafeFileHandle> locks)
    {
        try { stream.Dispose(); }
        finally
        {
            // A failed write or flush must not leave any destination volume locked. SafeFileHandle
            // closes do not throw on a native CloseHandle failure, preserving the stream exception.
            foreach (SafeFileHandle handle in locks) handle.Dispose();
            locks.Clear();
        }
    }

    private sealed class TargetSession(WindowsDiskAccess owner, DiskInfo target, SafeFileHandle physical,
        PhysicalDiskStream stream, List<SafeFileHandle> locks) : ITargetSession
    {
        private bool disposed;
        private bool mounted;
        private bool rawClosed;
        private string? mountedRoot;
        public Stream Stream => !rawClosed && !disposed ? stream : throw new ObjectDisposedException(nameof(TargetSession));

        public async Task<string> MountDataAsync(MigrationPlan plan, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (mounted) throw new InvalidOperationException("La partición de datos ya se ha montado.");
            if (plan.TargetSize != target.Size || plan.DataPartition.Index != 1)
                throw new IOException("El plan de particiones no coincide con el destino.");
            cancellationToken.ThrowIfCancellationRequested();
            stream.Flush();
            stream.Position = 0;
            byte[] createdMbr = new byte[512];
            stream.ReadExactly(createdMbr);
            string writtenFingerprint = Convert.ToHexString(SHA256.HashData(createdMbr));
            // Refresh while the original physical handle is valid. Old volumes remain locked until closing below.
            NativeDisk.Control(physical, NativeDisk.UpdateProperties);
            CloseRaw();
            await owner.RefreshAsync(target, cancellationToken).ConfigureAwait(false);
            if (NativeDisk.Fingerprint(target.Number, target.Size) != writtenFingerprint)
                throw new IOException("La tarjeta cambió al montar la partición. La restauración no se ha completado.");
            const string mountScript = """
                $disk = Get-Disk -Number $p.Number -ErrorAction Stop
                if ($disk.IsBoot -or $disk.IsSystem -or $disk.IsReadOnly -or
                    [long]$disk.Size -ne [long]$p.Size -or
                    ([string]$disk.SerialNumber).Trim() -cne [string]$p.SerialNumber -or
                    ([string]$disk.UniqueId).Trim() -cne [string]$p.UniqueId -or
                    [string]$disk.BusType -cne [string]$p.BusType -or
                    [int]$disk.LogicalSectorSize -ne 512 -or [string]$disk.PartitionStyle -ne 'MBR') {
                    throw 'El disco ha cambiado antes de montar la partición.'
                }
                Update-Disk -Number $p.Number -ErrorAction Stop
                $part = $null
                for ($attempt = 0; $attempt -lt 20; $attempt++) {
                    $candidate = @(Get-Partition -DiskNumber $p.Number -ErrorAction SilentlyContinue |
                        Where-Object { [long]$_.Offset -eq [long]$p.Offset -and [long]$_.Size -eq [long]$p.Length })
                    if ($candidate.Count -eq 1) { $part = $candidate[0]; break }
                    Start-Sleep -Milliseconds 250
                }
                if (-not $part -or $part.PartitionNumber -ne 1) { throw 'Windows no reconoce la partición de datos creada.' }
                if ($p.FileSystem -eq 'exFAT') {
                    $part | Format-Volume -FileSystem exFAT -NewFileSystemLabel 'NINMAGIC' -AllocationUnitSize 32768 -Confirm:$false -Force -ErrorAction Stop | Out-Null
                }
                if (-not $part.DriveLetter) {
                    $part | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction Stop | Out-Null
                }
                $part = Get-Partition -DiskNumber $p.Number -PartitionNumber 1 -ErrorAction Stop
                $vol = $part | Get-Volume -ErrorAction Stop
                if ([long]$part.Offset -ne [long]$p.Offset -or [long]$part.Size -ne [long]$p.Length -or
                    [string]$vol.FileSystemType -ine [string]$p.FileSystem -or -not $part.DriveLetter) {
                    throw 'No se ha podido comprobar la partición de destino montada.'
                }
                [pscustomobject]@{ Root=[string]$vol.Path; Number=[int]$part.DiskNumber;
                    Offset=[long]$part.Offset; Length=[long]$part.Size; FileSystem=[string]$vol.FileSystemType } | ConvertTo-Json -Compress
                """;
            string json = await PowerShellRunner.RunAsync(mountScript, new
            {
                target.Number, target.Size, target.SerialNumber, target.UniqueId, target.BusType,
                Offset = plan.DataPartition.Offset, Length = plan.DataPartition.Length, plan.FileSystem
            }, cancellationToken, 300).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement result = document.RootElement;
            string root = result.GetProperty("Root").GetString() ?? "";
            if (result.GetProperty("Number").GetInt32() != target.Number ||
                result.GetProperty("Offset").GetInt64() != plan.DataPartition.Offset ||
                result.GetProperty("Length").GetInt64() != plan.DataPartition.Length ||
                !IsVolumeGuidRoot(root))
                throw new IOException("Windows montó una unidad distinta a la esperada.");
            if (NativeDisk.Fingerprint(target.Number, target.Size) != writtenFingerprint)
                throw new IOException("La tarjeta cambió durante el montaje. La restauración no se ha completado.");
            NativeDisk.VolumeExtent? mountedVolume = NativeDisk.EnumerateVolumes()
                .SingleOrDefault(volume => string.Equals(volume.Path, root, StringComparison.OrdinalIgnoreCase));
            if (mountedVolume is null || mountedVolume.Extents.Count != 1 ||
                mountedVolume.Extents[0].Disk != target.Number ||
                mountedVolume.Extents[0].Offset != plan.DataPartition.Offset ||
                mountedVolume.Extents[0].Length != plan.DataPartition.Length)
                throw new IOException("El volumen montado no pertenece a la partición confirmada.");
            mounted = true;
            mountedRoot = root;
            return root;
        }

        private static bool IsVolumeGuidRoot(string root) => root.Length == 49 &&
            root.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) &&
            root.EndsWith("}\\", StringComparison.Ordinal) && Guid.TryParse(root.AsSpan(10, 38), out _);

        private void CloseRaw()
        {
            if (rawClosed) return;
            rawClosed = true;
            CloseRawResources(stream, locks);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (mountedRoot is not null) NativeDisk.FlushVolume(mountedRoot);
            }
            finally { CloseRaw(); }
        }
    }
}
