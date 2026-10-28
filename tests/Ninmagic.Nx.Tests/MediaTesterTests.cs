using Ninmagic.Nx.Core;

internal static class MediaTesterTests
{
    private const int MiB = 1024 * 1024;
    private static readonly IProgress<WorkProgress> Quiet = new ImmediateProgress(_ => { });

    public static async Task<int> RunAsync()
    {
        int passed = 0;
        async Task Run(string name, Func<Task> test)
        {
            await test();
            Console.WriteLine($"PASS media: {name}");
            passed++;
        }

        await Run("full write then flush then read, with bounded tail", async () =>
        {
            const int size = 4 * MiB + 512;
            using var media = new SimulatedMedia(size + 1024);
            Array.Fill(media.Storage, (byte)0xa5);
            media.Position = 17;
            var events = new List<WorkProgress>();
            DateTime started = DateTime.UtcNow;
            var result = await MediaTester.TestAsync(media, size, new ImmediateProgress(events.Add), CancellationToken.None);
            Check(result.BytesVerified == size, "Every requested byte must be verified.");
            Check(media.TotalWritten == size && media.TotalRead == size, "The full range must be written and read.");
            Check(media.FirstReadTotalWritten == size && media.FirstReadFlushCount > 0,
                "No read may precede completion and flush of the entire write pass.");
            Check(media.Storage.AsSpan(size).IndexOfAnyExcept((byte)0xa5) < 0, "The caller's upper bound must not be crossed.");
            Check(media.LargestRequest <= 4 * MiB, "I/O buffers must stay bounded for large devices.");
            Check(!media.Storage.AsSpan(0, 512).SequenceEqual(media.Storage.AsSpan(512, 512)), "Different addresses need different patterns.");
            Check(!media.Disposed, "The caller owns the stream lifetime.");
            Check(double.IsFinite(result.WriteMiBPerSecond) && result.WriteMiBPerSecond > 0 &&
                double.IsFinite(result.ReadMiBPerSecond) && result.ReadMiBPerSecond > 0, "Measurements must be finite and positive.");
            Check(result.CompletedUtc.Kind == DateTimeKind.Utc && result.CompletedUtc >= started && result.CompletedUtc <= DateTime.UtcNow,
                "Completion must carry an accurate UTC timestamp.");
            Check(result.Warning.Contains("lector", StringComparison.Ordinal) && result.Warning.Contains("No certifica", StringComparison.Ordinal),
                "A successful test must still describe the limits of its measurements.");
            Check(events.Any(p => p.Stage == "Probando tarjeta: escritura") && events.Any(p => p.Stage == "Probando tarjeta: lectura") &&
                events[^1].Stage == "Prueba de tarjeta superada", "Progress must distinguish write, read and completed stages.");
        });

        await Run("short nonzero reads are completed", async () =>
        {
            using var media = new SimulatedMedia(4 * MiB + 512) { MaxRead = 4093 };
            var result = await MediaTester.TestAsync(media, media.Length, Quiet, CancellationToken.None);
            Check(result.BytesVerified == media.Length && media.TotalRead == media.Length, "Partial reads must not truncate verification.");
        });

        await Run("wrapped fake capacity is rejected after full write", async () =>
        {
            using var media = new SimulatedMedia(8 * MiB, 4 * MiB);
            var error = await Throws<MediaTestException>(() => MediaTester.TestAsync(media, media.Length, Quiet, CancellationToken.None));
            Check(error.Phase == "verificación", "Aliased addresses must produce a verification failure.");
            Check(media.TotalWritten == media.Length && media.FirstReadTotalWritten == media.Length,
                "The full write pass must expose wraparound instead of testing individual blocks immediately.");
        });

        await Run("silently dropped writes are rejected", async () =>
        {
            using var media = new SimulatedMedia(1024) { DropWrites = true };
            var error = await Throws<MediaTestException>(() => MediaTester.TestAsync(media, media.Length, Quiet, CancellationToken.None));
            Check(error.Phase == "verificación", "A successful Write return cannot substitute for a matching reread.");
        });

        await Run("corruption reports the exact first address", async () =>
        {
            using var media = new SimulatedMedia(1024) { CorruptAt = 75 };
            var error = await Throws<MediaTestException>(() => MediaTester.TestAsync(media, media.Length, Quiet, CancellationToken.None));
            Check(error.Phase == "verificación" && error.ByteOffset == 75, "Report the corrupt byte, not only its buffer start.");
        });

        await Run("last byte of the final short buffer is verified", async () =>
        {
            const int size = 4 * MiB + 512;
            using var media = new SimulatedMedia(size) { CorruptAt = size - 1 };
            var error = await Throws<MediaTestException>(() => MediaTester.TestAsync(media, size, Quiet, CancellationToken.None));
            Check(error.ByteOffset == size - 1 && error.Phase == "verificación", "The tail of the device must be covered.");
        });

        await Run("unexpected end of stream fails", async () =>
        {
            using var media = new SimulatedMedia(1024) { MaxRead = 512, ZeroReadAt = 512 };
            var error = await Throws<MediaTestException>(() => MediaTester.TestAsync(media, media.Length, Quiet, CancellationToken.None));
            Check(error.Phase == "lectura" && error.ByteOffset == 512, "EOF must identify the first missing address.");
        });

        await Run("write and read I/O errors preserve their cause", async () =>
        {
            using var writeMedia = new SimulatedMedia(1024) { FailWrite = true };
            var writeError = await Throws<MediaTestException>(() => MediaTester.TestAsync(writeMedia, writeMedia.Length, Quiet, CancellationToken.None));
            Check(writeError.Phase == "escritura" && writeError.InnerException is IOException && writeMedia.TotalRead == 0,
                "Write failures must abort before any reading and retain diagnostics.");
            using var readMedia = new SimulatedMedia(1024) { FailRead = true };
            var readError = await Throws<MediaTestException>(() => MediaTester.TestAsync(readMedia, readMedia.Length, Quiet, CancellationToken.None));
            Check(readError.Phase == "lectura" && readError.InnerException is IOException,
                "Read access failures must be distinguished from data corruption.");
        });

        await Run("failed flush never produces a successful result", async () =>
        {
            using var media = new SimulatedMedia(1024) { FailFlush = true };
            var error = await Throws<MediaTestException>(() => MediaTester.TestAsync(media, media.Length, Quiet, CancellationToken.None));
            Check(error.Phase == "vaciado" && error.InnerException is IOException && media.TotalWritten == media.Length && media.TotalRead == 0,
                "Unconfirmed writes must not be accepted, even if an in-memory reread could match.");
        });

        await Run("already cancelled test writes nothing", async () =>
        {
            using var media = new SimulatedMedia(1024);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Throws<OperationCanceledException>(() => MediaTester.TestAsync(media, media.Length, Quiet, cts.Token));
            Check(media.TotalWritten == 0 && media.TotalRead == 0, "Cancellation before the test must leave the stream untouched.");
        });

        await Run("cancellation while writing aborts before reading", async () =>
        {
            using var media = new SimulatedMedia(8 * MiB);
            using var cts = new CancellationTokenSource();
            media.AfterWrite = cts.Cancel;
            await Throws<OperationCanceledException>(() => MediaTester.TestAsync(media, media.Length, Quiet, cts.Token));
            Check(media.TotalWritten > 0 && media.TotalWritten < media.Length && media.TotalRead == 0,
                "A cancelled write pass must not continue into verification.");
        });

        await Run("cancellation while reading never reports success", async () =>
        {
            using var media = new SimulatedMedia(8 * MiB);
            using var cts = new CancellationTokenSource();
            media.AfterRead = cts.Cancel;
            await Throws<OperationCanceledException>(() => MediaTester.TestAsync(media, media.Length, Quiet, cts.Token));
            Check(media.TotalWritten == media.Length && media.TotalRead > 0 && media.TotalRead < media.Length,
                "A cancelled partial verification must remain incomplete.");
        });

        await Run("cancellation at final progress cannot return success", async () =>
        {
            using var media = new SimulatedMedia(1024);
            using var cts = new CancellationTokenSource();
            var progress = new ImmediateProgress(p => { if (p.Stage == "Prueba de tarjeta superada") cts.Cancel(); });
            await Throws<OperationCanceledException>(() => MediaTester.TestAsync(media, media.Length, progress, cts.Token));
        });

        await Run("invalid sizes are rejected before mutation", async () =>
        {
            using var media = new SimulatedMedia(1024);
            foreach (long size in new long[] { -512, 0, 1, 513, 1536, long.MaxValue })
                await Throws<ArgumentOutOfRangeException>(() => MediaTester.TestAsync(media, size, Quiet, CancellationToken.None));
            Check(media.TotalWritten == 0 && media.TotalRead == 0, "Argument errors must not mutate the target.");
        });

        await Run("unsupported stream capabilities are rejected", async () =>
        {
            foreach (int disabled in new[] { 0, 1, 2 })
            {
                using var media = new SimulatedMedia(1024) { Readable = disabled != 0, Writable = disabled != 1, Seekable = disabled != 2 };
                await Throws<ArgumentException>(() => MediaTester.TestAsync(media, media.Length, Quiet, CancellationToken.None));
                Check(media.TotalWritten == 0, "Unsupported streams must be rejected before mutation.");
            }
        });

        await Run("null arguments are rejected", async () =>
        {
            using var media = new SimulatedMedia(1024);
            await Throws<ArgumentNullException>(() => MediaTester.TestAsync(null!, 1024, Quiet, CancellationToken.None));
            await Throws<ArgumentNullException>(() => MediaTester.TestAsync(media, 1024, null!, CancellationToken.None));
            Check(media.TotalWritten == 0, "Missing arguments must not mutate the target.");
        });

        await Run("incorrect seek positioning is rejected", async () =>
        {
            using var media = new SimulatedMedia(1024) { WrongSeekResult = true };
            var error = await Throws<MediaTestException>(() => MediaTester.TestAsync(media, media.Length, Quiet, CancellationToken.None));
            Check(error.Phase == "escritura" && error.ByteOffset == 0 && media.TotalWritten == 0,
                "A stream that cannot reach the requested address cannot be tested safely.");
        });

        await Run("new run uses fresh unpredictable patterns", async () =>
        {
            using var first = new SimulatedMedia(512);
            using var second = new SimulatedMedia(512);
            await MediaTester.TestAsync(first, first.Length, Quiet, CancellationToken.None);
            await MediaTester.TestAsync(second, second.Length, Quiet, CancellationToken.None);
            Check(!first.Storage.AsSpan().SequenceEqual(second.Storage), "A new random key must prevent reusing stale test content.");
        });

        return passed;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class ImmediateProgress(Action<WorkProgress> callback) : IProgress<WorkProgress>
    {
        public void Report(WorkProgress value) => callback(value);
    }

    /// <summary>Array-backed fake disk. It never enumerates or opens a device or file.</summary>
    private sealed class SimulatedMedia : Stream
    {
        private readonly long length;
        private long position;
        public SimulatedMedia(int advertisedLength, int? actualLength = null)
        {
            length = advertisedLength;
            Storage = new byte[actualLength ?? advertisedLength];
        }

        public byte[] Storage { get; }
        public int MaxRead { get; init; } = int.MaxValue;
        public long? ZeroReadAt { get; init; }
        public long? CorruptAt { get; init; }
        public bool DropWrites { get; init; }
        public bool FailWrite { get; init; }
        public bool FailRead { get; init; }
        public bool FailFlush { get; init; }
        public bool WrongSeekResult { get; init; }
        public bool Readable { get; init; } = true;
        public bool Writable { get; init; } = true;
        public bool Seekable { get; init; } = true;
        public Action? AfterWrite { get; set; }
        public Action? AfterRead { get; set; }
        public long TotalWritten { get; private set; }
        public long TotalRead { get; private set; }
        public long FirstReadTotalWritten { get; private set; } = -1;
        public int FirstReadFlushCount { get; private set; }
        public int FlushCount { get; private set; }
        public int LargestRequest { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => Readable && !Disposed;
        public override bool CanWrite => Writable && !Disposed;
        public override bool CanSeek => Seekable && !Disposed;
        public override long Length => length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!CanSeek) throw new NotSupportedException();
            long next = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, SeekOrigin.End => length + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
            if (next < 0 || next > length) throw new IOException("Seek outside the simulated medium.");
            position = next;
            return WrongSeekResult ? position + 1 : position;
        }

        public override int Read(Span<byte> buffer)
        {
            if (!CanRead) throw new NotSupportedException();
            if (FailRead) throw new IOException("Simulated reader disconnected.");
            if (FirstReadTotalWritten < 0) { FirstReadTotalWritten = TotalWritten; FirstReadFlushCount = FlushCount; }
            LargestRequest = Math.Max(LargestRequest, buffer.Length);
            if (ZeroReadAt is long end && position >= end) return 0;
            int count = (int)Math.Min(Math.Min(buffer.Length, MaxRead), length - position);
            if (ZeroReadAt is long stop) count = (int)Math.Min(count, stop - position);
            long start = position;
            for (int done = 0; done < count;)
            {
                int physical = (int)(position % Storage.Length);
                int piece = Math.Min(count - done, Storage.Length - physical);
                Storage.AsSpan(physical, piece).CopyTo(buffer[done..]);
                done += piece;
                position += piece;
            }
            if (CorruptAt is long corrupt && corrupt >= start && corrupt < start + count) buffer[(int)(corrupt - start)] ^= 0xff;
            TotalRead += count;
            AfterRead?.Invoke();
            return count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!CanWrite) throw new NotSupportedException();
            if (FailWrite) throw new IOException("Simulated write error.");
            if (buffer.Length > length - position) throw new IOException("Write outside the simulated medium.");
            LargestRequest = Math.Max(LargestRequest, buffer.Length);
            for (int done = 0; done < buffer.Length;)
            {
                int physical = (int)(position % Storage.Length);
                int piece = Math.Min(buffer.Length - done, Storage.Length - physical);
                if (!DropWrites) buffer.Slice(done, piece).CopyTo(Storage.AsSpan(physical, piece));
                done += piece;
                position += piece;
            }
            TotalWritten += buffer.Length;
            AfterWrite?.Invoke();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
            if (FailFlush) throw new IOException("Simulated flush failure.");
            FlushCount++;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
