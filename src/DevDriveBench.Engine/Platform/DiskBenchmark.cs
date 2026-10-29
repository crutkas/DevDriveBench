// Ported from crutkas/devdrive (branch crutkas-drive-space-analyzer), MIT License.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DevDriveBench.Engine.Abstractions;
using DevDriveBench.Engine.Model;
using Microsoft.Win32.SafeHandles;

namespace DevDriveBench.Engine.Platform;

/// <summary>
/// Real <see cref="IDiskBenchmark"/>. Measures genuine, cache-bypassing disk performance using Win32
/// <c>CreateFile</c> with <c>FILE_FLAG_NO_BUFFERING</c> (reads) and additionally
/// <c>FILE_FLAG_WRITE_THROUGH</c> (writes), I/O issued through a page-aligned buffer
/// (<c>VirtualAlloc</c>) so the unbuffered alignment requirements are satisfied.
/// </summary>
/// <remarks>
/// <para><b>SAFETY:</b> the benchmark's temporary file is the only disk write the application makes.
/// It is created under a <c>DevDriveBench.Disk</c> folder on the target drive, bounded to
/// <see cref="DiskBenchmarkOptions.FileSizeBytes"/> (default 128 MiB), guarded by a free-space check,
/// and ALWAYS deleted in a <c>finally</c> block (the folder too, when this run created it).</para>
/// <para>All offsets/lengths are 4 KiB-aligned so they satisfy <c>NO_BUFFERING</c> on both NTFS and ReFS.</para>
/// </remarks>
public sealed class DiskBenchmark : IDiskBenchmark
{
    private const string BenchFolderName = WorkloadPaths.DiskFolderName;
    private const int Sector = 4096;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint CREATE_ALWAYS = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_BEGIN = 0;

    private const uint MEM_COMMIT = 0x00001000;
    private const uint MEM_RESERVE = 0x00002000;
    private const uint MEM_RELEASE = 0x00008000;
    private const uint PAGE_READWRITE = 0x04;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, IntPtr lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    /// <inheritdoc />
    public DiskBenchmarkResult Run(string driveRoot, DiskBenchmarkOptions options, IDiskBenchmarkObserver? observer = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveRoot);
        options ??= DiskBenchmarkOptions.Default;

        long fileSize = AlignDown(options.FileSizeBytes, Sector);
        int seqBlock = (int)AlignDown(options.SequentialBlockBytes, Sector);
        if (seqBlock < Sector)
        {
            seqBlock = Sector;
        }

        int randBlock = (int)AlignDown(options.RandomBlockBytes, Sector);
        if (randBlock < Sector)
        {
            randBlock = Sector;
        }

        if (fileSize < seqBlock)
        {
            fileSize = seqBlock;
        }

        GuardFreeSpace(driveRoot, fileSize, options.FreeSpaceMarginBytes);

        string workingDirectory = Path.Combine(driveRoot, BenchFolderName);
        bool createdDirectory = !Directory.Exists(workingDirectory);
        Directory.CreateDirectory(workingDirectory);
        string filePath = Path.Combine(workingDirectory, $"ddm-bench-{Guid.NewGuid():N}.tmp");

        // F5: the random pass uses randBlock, which can exceed seqBlock. Size (and fill) the single
        // shared I/O buffer to the LARGER of the two so MeasureRandom never reads/writes past its end.
        int bufferBytes = Math.Max(seqBlock, randBlock);
        IntPtr buffer = VirtualAlloc(IntPtr.Zero, (UIntPtr)(uint)bufferBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (buffer == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate an aligned I/O buffer for the disk speed test.");
        }

        try
        {
            FillBuffer(buffer, bufferBytes);

            int samples = Math.Max(1, options.SamplesPerPass);
            double seqWrite = MeasureSequentialWrite(filePath, fileSize, seqBlock, buffer, observer, samples, cancellationToken);
            double seqRead = MeasureSequentialRead(filePath, fileSize, seqBlock, buffer, observer, samples, cancellationToken);
            double randRead = MeasureRandom(filePath, fileSize, randBlock, buffer, options.RandomOperations, write: false, observer, samples, cancellationToken);
            double randWrite = MeasureRandom(filePath, fileSize, randBlock, buffer, options.RandomOperations, write: true, observer, samples, cancellationToken);

            return new DiskBenchmarkResult
            {
                DriveRoot = NormalizeRoot(driveRoot),
                SequentialWriteMBps = seqWrite,
                SequentialReadMBps = seqRead,
                RandomRead4KIops = randRead,
                RandomWrite4KIops = randWrite,
            };
        }
        finally
        {
            VirtualFree(buffer, UIntPtr.Zero, MEM_RELEASE);
            TryDeleteFile(filePath);
            TryRemoveEmptyDirectory(workingDirectory, createdDirectory);
        }
    }

    private static double MeasureSequentialWrite(string path, long fileSize, int block, IntPtr buffer, IDiskBenchmarkObserver? observer, int samples, CancellationToken ct)
    {
        using SafeFileHandle handle = CreateFile(
            path, GENERIC_WRITE, FILE_SHARE_READ, IntPtr.Zero, CREATE_ALWAYS,
            FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH | FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw LastError("open the test file for sequential write");
        }

        var sampler = new WindowSampler(observer, DiskMetric.SequentialWrite, fileSize, Math.Max(1L, fileSize / block / samples) * block, bytesPerUnit: 1_000_000d);
        observer?.MetricStarted(DiskMetric.SequentialWrite);
        long written = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (written < fileSize)
        {
            ct.ThrowIfCancellationRequested();
            int toWrite = (int)Math.Min(block, fileSize - written);
            if (!WriteFile(handle, buffer, (uint)toWrite, out uint n, IntPtr.Zero) || n == 0)
            {
                throw LastError("sequential WriteFile");
            }

            written += n;
            sampler.Add(n, written, stopwatch);
        }

        FlushFileBuffers(handle);
        stopwatch.Stop();
        double result = ThroughputMBps(fileSize, stopwatch.Elapsed);
        observer?.MetricCompleted(DiskMetric.SequentialWrite, result);
        return result;
    }

    private static double MeasureSequentialRead(string path, long fileSize, int block, IntPtr buffer, IDiskBenchmarkObserver? observer, int samples, CancellationToken ct)
    {
        using SafeFileHandle handle = CreateFile(
            path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_NO_BUFFERING | FILE_FLAG_SEQUENTIAL_SCAN | FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw LastError("open the test file for sequential read");
        }

        var sampler = new WindowSampler(observer, DiskMetric.SequentialRead, fileSize, Math.Max(1L, fileSize / block / samples) * block, bytesPerUnit: 1_000_000d);
        observer?.MetricStarted(DiskMetric.SequentialRead);
        long read = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (read < fileSize)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(block, fileSize - read);
            if (!ReadFile(handle, buffer, (uint)toRead, out uint n, IntPtr.Zero) || n == 0)
            {
                throw LastError("sequential ReadFile");
            }

            read += n;
            sampler.Add(n, read, stopwatch);
        }

        stopwatch.Stop();
        double result = ThroughputMBps(fileSize, stopwatch.Elapsed);
        observer?.MetricCompleted(DiskMetric.SequentialRead, result);
        return result;
    }

    private static double MeasureRandom(string path, long fileSize, int block, IntPtr buffer, int operations, bool write, IDiskBenchmarkObserver? observer, int samples, CancellationToken ct)
    {
        uint access = write ? GENERIC_WRITE : GENERIC_READ;
        uint flags = FILE_FLAG_NO_BUFFERING | FILE_ATTRIBUTE_NORMAL | (write ? FILE_FLAG_WRITE_THROUGH : 0u);
        DiskMetric metric = write ? DiskMetric.RandomWrite4K : DiskMetric.RandomRead4K;

        using SafeFileHandle handle = CreateFile(
            path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw LastError(write ? "open the test file for random write" : "open the test file for random read");
        }

        long blocks = fileSize / block;
        if (blocks <= 0)
        {
            return 0d;
        }

        // Deterministic offsets (fixed seed): we are timing the device, not the randomness.
        var rng = new Random(20240607);
        int ops = Math.Max(1, operations);

        // Units are operations: each op counts as 1 "byte" with bytesPerUnit = 1, so the window rate is IOPS.
        var sampler = new WindowSampler(observer, metric, ops, Math.Max(1, ops / samples), bytesPerUnit: 1d);
        observer?.MetricStarted(metric);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < ops; i++)
        {
            ct.ThrowIfCancellationRequested();
            long offset = rng.NextInt64(blocks) * block;
            if (!SetFilePointerEx(handle, offset, IntPtr.Zero, FILE_BEGIN))
            {
                throw LastError("SetFilePointerEx (random)");
            }

            bool ok = write
                ? WriteFile(handle, buffer, (uint)block, out _, IntPtr.Zero)
                : ReadFile(handle, buffer, (uint)block, out _, IntPtr.Zero);
            if (!ok)
            {
                throw LastError(write ? "random WriteFile" : "random ReadFile");
            }

            sampler.Add(1, i + 1, stopwatch);
        }

        if (write)
        {
            FlushFileBuffers(handle);
        }

        stopwatch.Stop();
        double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 1e-6);
        double result = ops / seconds;
        observer?.MetricCompleted(metric, result);
        return result;
    }

    /// <summary>
    /// Reports the rate over each window of <c>windowUnits</c> to the observer. Allocation-free and only
    /// touches the observer once per window, so it adds negligible overhead to the timed loop.
    /// </summary>
    private struct WindowSampler
    {
        private readonly IDiskBenchmarkObserver? _observer;
        private readonly DiskMetric _metric;
        private readonly double _total;
        private readonly long _windowUnits;
        private readonly double _unitsPerValue;
        private long _inWindow;
        private TimeSpan _windowStart;

        public WindowSampler(IDiskBenchmarkObserver? observer, DiskMetric metric, long total, long windowUnits, double bytesPerUnit)
        {
            _observer = observer;
            _metric = metric;
            _total = Math.Max(1L, total);
            _windowUnits = Math.Max(1L, windowUnits);
            _unitsPerValue = bytesPerUnit;
            _inWindow = 0;
            _windowStart = TimeSpan.Zero;
        }

        public void Add(long units, long done, Stopwatch stopwatch)
        {
            if (_observer is null)
            {
                return;
            }

            _inWindow += units;
            if (_inWindow < _windowUnits && done < _total)
            {
                return;
            }

            TimeSpan now = stopwatch.Elapsed;
            double seconds = Math.Max((now - _windowStart).TotalSeconds, 1e-6);
            _observer.Sample(_metric, Math.Min(1d, done / _total), _inWindow / _unitsPerValue / seconds);
            _inWindow = 0;
            _windowStart = now;
        }
    }

    private static void GuardFreeSpace(string driveRoot, long fileSize, long marginBytes)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(driveRoot));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < fileSize + marginBytes)
            {
                long needMb = (fileSize + marginBytes) / (1024 * 1024);
                throw new InvalidOperationException(
                    $"Not enough free space on {root} to run the disk speed test (needs about {needMb} MB free).");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // DriveInfo can throw on exotic roots; proceed best-effort (write errors will surface anyway).
        }
    }

    private static double ThroughputMBps(long bytes, TimeSpan elapsed)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 1e-6);
        return bytes / seconds / 1_000_000d;
    }

    private static long AlignDown(long value, int alignment) => value - (value % alignment);

    private static string NormalizeRoot(string driveRoot)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(driveRoot)) ?? driveRoot;
        }
        catch
        {
            return driveRoot;
        }
    }

    private static void FillBuffer(IntPtr buffer, int size)
    {
        // Pseudo-random bytes so the writes aren't trivially compressible by the storage stack.
        var rng = new Random(7);
        byte[] scratch = new byte[size];
        rng.NextBytes(scratch);
        Marshal.Copy(scratch, 0, buffer, size);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; the temp file is uniquely named so a stray one is harmless.
        }
    }

    private static void TryRemoveEmptyDirectory(string directory, bool createdByThisRun)
    {
        if (!createdByThisRun)
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static Exception LastError(string what)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"Disk speed test failed while trying to {what} (Win32 error {code}).");
    }
}
