using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ninmagic.Nx.Windows;

internal static class NativeDisk
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 3, OpenExisting = 3, WriteThrough = 0x80000000;
    private const uint GetLengthInfo = 0x0007405C, GetDeviceNumber = 0x002D1080;
    private const uint GetDiskExtents = 0x00560000;
    internal const uint LockVolume = 0x00090018, UnlockVolume = 0x0009001C, DismountVolume = 0x00090020;
    internal const uint UpdateProperties = 0x00070140;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize,
        [Out] byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolumeW(StringBuilder name, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolumeW(IntPtr search, StringBuilder name, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr search);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);

    internal static SafeFileHandle OpenPhysical(int number, bool write, long expectedLength)
    {
        var handle = Open($"\\\\.\\PhysicalDrive{number}", write ? GenericRead | GenericWrite : GenericRead,
            write ? WriteThrough : 0);
        try
        {
            byte[] length = Query(handle, GetLengthInfo, 8);
            byte[] device = Query(handle, GetDeviceNumber, 12);
            if (BitConverter.ToInt64(length) != expectedLength || BitConverter.ToUInt32(device, 4) != number)
                throw new IOException("El disco ha cambiado desde la selección. Vuelve a buscar las tarjetas.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle Open(string path, uint access, uint flags = 0)
    {
        SafeFileHandle handle = CreateFileW(path, access, ShareReadWrite, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException($"No se puede abrir {path}: {new Win32Exception(error).Message}", new Win32Exception(error));
    }

    internal static byte[] Query(SafeFileHandle handle, uint code, int length)
    {
        byte[] result = new byte[length];
        if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, result, (uint)result.Length, out uint returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (returned < length && code != GetDiskExtents) throw new IOException("Windows devolvió datos incompletos del disco.");
        if (code == GetDiskExtents)
        {
            if (returned < 8 || returned > result.Length) throw new IOException("Windows devolvió un mapa de volumen incompleto.");
            Array.Resize(ref result, (int)returned);
        }
        return result;
    }

    internal static void Control(SafeFileHandle handle, uint code)
    {
        if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static string Fingerprint(int number, long size)
    {
        using SafeFileHandle handle = OpenPhysical(number, false, size);
        using var stream = new PhysicalDiskStream(handle, size, writable: false);
        byte[] sector = new byte[512];
        stream.ReadExactly(sector);
        return Convert.ToHexString(SHA256.HashData(sector));
    }

    internal sealed record VolumeExtent(string Path, IReadOnlyList<(uint Disk, long Offset, long Length)> Extents);

    internal static IReadOnlyList<VolumeExtent> EnumerateVolumes()
    {
        var result = new List<VolumeExtent>();
        var name = new StringBuilder(1024);
        IntPtr search = FindFirstVolumeW(name, (uint)name.Capacity);
        if (search == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            do
            {
                string path = name.ToString();
                // CD/DVD volumes are not backed by a writable physical disk.
                if (GetDriveTypeW(path) == 5) continue;
                using SafeFileHandle volume = Open(path.TrimEnd('\\'), 0);
                byte[] extents;
                try { extents = Query(volume, GetDiskExtents, 65536); }
                catch (Win32Exception e) when (e.NativeErrorCode is 1 or 21 or 50) { continue; }
                uint count = BitConverter.ToUInt32(extents);
                if (count == 0 || count > (extents.Length - 8) / 24)
                    throw new IOException("Windows devolvió un mapa de volumen no válido.");
                var parsed = new List<(uint, long, long)>();
                for (int index = 0; index < count; index++)
                {
                    int offset = 8 + index * 24;
                    parsed.Add((BitConverter.ToUInt32(extents, offset), BitConverter.ToInt64(extents, offset + 8),
                        BitConverter.ToInt64(extents, offset + 16)));
                }
                result.Add(new VolumeExtent(path, parsed));
            } while (FindNextVolumeW(search, name, (uint)name.Capacity));
            int error = Marshal.GetLastWin32Error();
            if (error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES
        }
        finally { FindVolumeClose(search); }
        return result;
    }

    internal static SafeFileHandle LockAndDismount(string volumePath)
    {
        SafeFileHandle handle = Open(volumePath.TrimEnd('\\'), GenericRead | GenericWrite);
        try
        {
            Control(handle, LockVolume);
            Control(handle, DismountVolume);
            return handle;
        }
        catch (Exception e)
        {
            handle.Dispose();
            throw new IOException("No se pudo bloquear la tarjeta de destino. Cierra el Explorador, aplicaciones y cualquier archivo abierto de esa tarjeta.", e);
        }
    }

    internal static void FlushVolume(string volumePath)
    {
        using SafeFileHandle handle = Open(volumePath.TrimEnd('\\'), GenericRead | GenericWrite);
        if (!FlushFileBuffers(handle))
            throw new IOException("Windows no ha podido confirmar la escritura final de la tarjeta.",
                new Win32Exception(Marshal.GetLastWin32Error()));
    }
}

internal sealed class PhysicalDiskStream : Stream
{
    private const int SectorSize = 512;
    private readonly Stream inner;
    private readonly long length;
    private readonly bool writable;
    private long position;
    private bool disposed;
    internal PhysicalDiskStream(SafeFileHandle handle, long length, bool writable)
        : this(OpenStream(handle, length, writable), length, writable) { }

    // The injected stream permits testing readers that enforce the same sector constraints as a device.
    internal PhysicalDiskStream(Stream device, long length, bool writable)
    {
        if (length <= 0 || length % SectorSize != 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (!device.CanRead || !device.CanSeek || writable && !device.CanWrite)
            throw new ArgumentException("El dispositivo no admite el acceso solicitado.", nameof(device));
        this.length = length;
        this.writable = writable;
        inner = device;
    }
    private static FileStream OpenStream(SafeFileHandle handle, long length, bool writable)
    {
        if (length <= 0 || length % SectorSize != 0) throw new ArgumentOutOfRangeException(nameof(length));
        // No FileStream read-ahead or deferred writes. Every device read below is sector aligned,
        // including short metadata reads, so Windows never receives an 8-byte physical-disk request.
        return new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read, bufferSize: 1, isAsync: false);
    }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => writable && inner.CanWrite;
    public override long Length => length;
    public override long Position { get { ObjectDisposedException.ThrowIf(disposed, this); return position; } set => Seek(value, SeekOrigin.Begin); }
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (inner is FileStream file) file.Flush(flushToDisk: true);
        else inner.Flush();
    }
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask;
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int wanted = ClampRead(buffer.Length), copied = 0;
        Span<byte> sector = stackalloc byte[SectorSize];
        while (copied < wanted)
        {
            int skip = (int)(position % SectorSize), remaining = wanted - copied;
            inner.Position = position - skip;
            if (skip == 0 && remaining >= SectorSize)
            {
                int count = inner.Read(buffer.Slice(copied, remaining / SectorSize * SectorSize));
                CheckAlignedRead(count);
                position += count; copied += count;
            }
            else
            {
                int count = inner.Read(sector);
                if (count != SectorSize) throw new IOException("Windows devolvió un sector incompleto de la tarjeta.");
                int take = Math.Min(remaining, SectorSize - skip);
                sector.Slice(skip, take).CopyTo(buffer[copied..]);
                position += take; copied += take;
            }
        }
        return copied;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        int wanted = ClampRead(buffer.Length), copied = 0;
        byte[]? sector = null;
        while (copied < wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int skip = (int)(position % SectorSize), remaining = wanted - copied;
            inner.Position = position - skip;
            if (skip == 0 && remaining >= SectorSize)
            {
                int count = await inner.ReadAsync(buffer.Slice(copied, remaining / SectorSize * SectorSize), cancellationToken).ConfigureAwait(false);
                CheckAlignedRead(count);
                position += count; copied += count;
            }
            else
            {
                sector ??= new byte[SectorSize];
                int count = await inner.ReadAsync(sector, cancellationToken).ConfigureAwait(false);
                if (count != SectorSize) throw new IOException("Windows devolvió un sector incompleto de la tarjeta.");
                int take = Math.Min(remaining, SectorSize - skip);
                sector.AsMemory(skip, take).CopyTo(buffer[copied..]);
                position += take; copied += take;
            }
        }
        return copied;
    }
    private static void CheckAlignedRead(int count)
    {
        if (count <= 0 || count % SectorSize != 0)
            throw new IOException("Windows devolvió datos incompletos de la tarjeta.");
    }
    private int ClampRead(int count) => (int)Math.Min(count, Math.Max(0, length - Position));
    private void CheckWrite(int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!writable) throw new NotSupportedException("El origen está abierto solo para lectura.");
        if (count < 0 || Position > length - count) throw new IOException("La escritura supera los límites del destino.");
        // Never expand an unaligned write or read-modify-write neighbouring sectors.
        if (count != 0 && (Position % SectorSize != 0 || count % SectorSize != 0))
            throw new IOException("La escritura física debe estar alineada a sectores de 512 bytes.");
    }
    public override void Write(byte[] buffer, int offset, int count)
    { ValidateBufferArguments(buffer, offset, count); Write(buffer.AsSpan(offset, count)); }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckWrite(buffer.Length);
        if (buffer.Length == 0) return;
        inner.Position = position; inner.Write(buffer); position += buffer.Length;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckWrite(buffer.Length); cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0) return;
        inner.Position = position;
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        position += buffer.Length;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long position = checked(origin switch
        {
            SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        });
        if (position < 0 || position > length) throw new IOException("Acceso fuera de los límites de la tarjeta.");
        this.position = position;
        return position;
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed) { disposed = true; inner.Dispose(); }
        base.Dispose(disposing);
    }
}
