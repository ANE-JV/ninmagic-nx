using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Ninmagic.Nx.Core;

public sealed record MediaTestResult(long BytesVerified, double WriteMiBPerSecond,
    double ReadMiBPerSecond, DateTime CompletedUtc, string Warning);

/// <summary>A data-integrity failure in a requested byte range, not a diagnosis of the card's brand or reader.</summary>
public sealed class MediaTestException : IOException
{
    public string Phase { get; }
    public long ByteOffset { get; }

    public MediaTestException(string phase, long byteOffset, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Phase = phase;
        ByteOffset = byteOffset;
    }
}

/// <summary>
/// Destructively tests the supplied stream's [0, size) range. The caller must obtain informed target
/// confirmation and lock the destination first. This class cannot discover or open any disk itself.
/// </summary>
public static class MediaTester
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const double MiB = 1024 * 1024;
    private const string WriteStage = "Probando tarjeta: escritura";
    private const string ReadStage = "Probando tarjeta: lectura";

    public static async Task<MediaTestResult> TestAsync(Stream stream, long size,
        IProgress<WorkProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(progress);
        if (size <= 0 || size % 512 != 0)
            throw new ArgumentOutOfRangeException(nameof(size), "El tamaño de la prueba debe ser un múltiplo positivo de 512 bytes.");
        if (!stream.CanSeek || !stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("La prueba necesita un destino bloqueado con lectura, escritura y acceso aleatorio.", nameof(stream));
        if (stream.Length < size)
            throw new ArgumentOutOfRangeException(nameof(size), "La prueba no puede superar la longitud del destino.");
        cancellationToken.ThrowIfCancellationRequested();

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.GenerateKey();
        byte[] counters = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] expected = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] actual = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            // Each 16-byte counter contains its absolute position and a domain marker. A random AES
            // key per run makes the output address-dependent and difficult to compress or predict.
            // The full write pass MUST finish before the first read: immediate write/read pairs can
            // incorrectly pass a fake card whose advertised addresses wrap around smaller storage.
            var writeTime = Stopwatch.StartNew();
            progress.Report(new(WriteStage, "Escribiendo patrones en toda la capacidad anunciada; se borran los datos anteriores del destino.", 0, size));
            cancellationToken.ThrowIfCancellationRequested();
            Seek(stream, 0, "escritura");
            long lastReport = Environment.TickCount64;
            for (long offset = 0; offset < size;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(BufferSize, size - offset);
                FillPattern(aes, counters, expected, count, offset);
                try { await stream.WriteAsync(expected.AsMemory(0, count), cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (IsMediaIoError(ex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw Failure("escritura", offset, "No se pudo escribir el patrón de prueba", ex);
                }
                offset += count;
                Report(progress, WriteStage, "Escribiendo toda la capacidad antes de comprobarla.", offset, size, ref lastReport);
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new(WriteStage, "Vaciando la escritura antes de leer y comprobar los datos.", size, size));
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The Windows adapter's Stream.Flush also calls FlushFileBuffers. A plain FileStream
                // is explicitly flushed to storage, rather than just its managed buffer.
                if (stream is FileStream file) file.Flush(flushToDisk: true);
                else stream.Flush();
            }
            catch (Exception ex) when (IsMediaIoError(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw Failure("vaciado", size, "Windows no pudo confirmar el vaciado de la escritura", ex);
            }
            cancellationToken.ThrowIfCancellationRequested();
            writeTime.Stop();

            var readTime = Stopwatch.StartNew();
            progress.Report(new(ReadStage, "Leyendo de nuevo toda la capacidad para detectar errores o capacidad falsa.", 0, size));
            cancellationToken.ThrowIfCancellationRequested();
            Seek(stream, 0, "lectura");
            lastReport = Environment.TickCount64;
            for (long offset = 0; offset < size;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(BufferSize, size - offset);
                FillPattern(aes, counters, expected, count, offset);
                int read = 0;
                while (read < count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int received;
                    try { received = await stream.ReadAsync(actual.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) when (IsMediaIoError(ex))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw Failure("lectura", offset + read, "No se pudieron leer los datos de prueba", ex);
                    }
                    if (received <= 0 || received > count - read)
                        throw Failure("lectura", offset + read, "La lectura terminó antes de comprobar toda la capacidad");
                    read += received;
                }
                if (!actual.AsSpan(0, count).SequenceEqual(expected.AsSpan(0, count)))
                {
                    int mismatch = FirstMismatch(actual, expected, count);
                    throw Failure("verificación", offset + mismatch, "Los datos leídos no coinciden con los escritos; puede haber capacidad falsa o errores en la tarjeta, el lector o la conexión");
                }
                offset += count;
                Report(progress, ReadStage, "Comprobando los patrones y sus posiciones originales.", offset, size, ref lastReport);
            }
            cancellationToken.ThrowIfCancellationRequested();
            readTime.Stop();
            double writeRate = Rate(size, writeTime.Elapsed);
            double readRate = Rate(size, readTime.Elapsed);
            string warning = readRate * 1.048576 < 60
                ? "Lectura observada durante la prueba inferior a 60 MB/s; no demuestra por sí sola que la tarjeta sea defectuosa. "
                : "";
            warning += "Las velocidades observadas incluyen la generación y comprobación de los patrones; también dependen del lector, de la conexión USB y del PC. La prueba verifica capacidad e integridad en esta ejecución. No certifica la marca, la vida útil, el acceso aleatorio ni el rendimiento de emuMMC en la consola.";
            progress.Report(new("Prueba de tarjeta superada", "Toda la capacidad solicitada coincide con los patrones escritos.", size, size));
            cancellationToken.ThrowIfCancellationRequested();
            return new(size, writeRate, readRate, DateTime.UtcNow, warning);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(counters, clearArray: true);
            ArrayPool<byte>.Shared.Return(expected, clearArray: true);
            ArrayPool<byte>.Shared.Return(actual, clearArray: true);
        }
    }

    private static void FillPattern(Aes aes, byte[] counters, byte[] output, int count, long offset)
    {
        ulong counter = (ulong)(offset / 16);
        for (int at = 0; at < count; at += 16)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(counters.AsSpan(at, 8), counter++);
            "NX-CARD!"u8.CopyTo(counters.AsSpan(at + 8, 8));
        }
        int written = aes.EncryptEcb(counters.AsSpan(0, count), output.AsSpan(0, count), PaddingMode.None);
        if (written != count) throw new CryptographicException("No se pudo generar el patrón completo de la prueba.");
    }

    private static void Seek(Stream stream, long offset, string phase)
    {
        try
        {
            if (stream.Seek(offset, SeekOrigin.Begin) != offset)
                throw new IOException("El destino no permite situarse en el sector solicitado.");
        }
        catch (Exception ex) when (IsMediaIoError(ex))
        { throw Failure(phase, offset, "No se pudo acceder a la posición de prueba", ex); }
    }

    private static void Report(IProgress<WorkProgress> progress, string stage, string detail,
        long completed, long total, ref long lastReport)
    {
        long now = Environment.TickCount64;
        if (completed == total || now - lastReport >= 150)
        {
            progress.Report(new(stage, detail, completed, total));
            lastReport = now;
        }
    }

    private static int FirstMismatch(byte[] actual, byte[] expected, int count)
    {
        for (int at = 0; at < count; at++) if (actual[at] != expected[at]) return at;
        throw new InvalidOperationException("No se pudo localizar la discrepancia de la prueba.");
    }

    private static double Rate(long bytes, TimeSpan elapsed) => bytes / MiB / Math.Max(elapsed.TotalSeconds, 1.0 / Stopwatch.Frequency);
    private static bool IsMediaIoError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;
    private static MediaTestException Failure(string phase, long offset, string reason, Exception? inner = null) =>
        new(phase, offset, $"{reason} (fase: {phase}, posición: {offset:N0} bytes). La prueba ha fallado y no debe continuar la restauración. El contenido anterior del destino ya puede haberse borrado.", inner);
}
