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
        using var stream = new FileStream(handle, FileAccess.Read, 4096, false);
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
    private readonly FileStream inner;
    private readonly long length;
    private readonly bool writable;
    internal PhysicalDiskStream(SafeFileHandle handle, long length, bool writable)
    {
        this.length = length;
        this.writable = writable;
        inner = new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read, 1024 * 1024, false);
    }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => writable && inner.CanWrite;
    public override long Length => length;
    public override long Position { get => inner.Position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() => inner.Flush(flushToDisk: true);
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask;
    }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, ClampRead(count));
    public override int Read(Span<byte> buffer) => inner.Read(buffer[..ClampRead(buffer.Length)]);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer[..ClampRead(buffer.Length)], cancellationToken);
    private int ClampRead(int count) => (int)Math.Min(count, Math.Max(0, length - Position));
    private void CheckWrite(int count)
    {
        if (!writable) throw new NotSupportedException("El origen está abierto solo para lectura.");
        if (count < 0 || Position > length - count) throw new IOException("La escritura supera los límites del destino.");
    }
    public override void Write(byte[] buffer, int offset, int count) { CheckWrite(count); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { CheckWrite(buffer.Length); inner.Write(buffer); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { CheckWrite(buffer.Length); return inner.WriteAsync(buffer, cancellationToken); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = checked(origin switch
        {
            SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        });
        if (position < 0 || position > length) throw new IOException("Acceso fuera de los límites de la tarjeta.");
        return inner.Seek(position, SeekOrigin.Begin);
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
