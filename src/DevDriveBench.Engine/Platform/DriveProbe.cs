using System.Management;
using System.Runtime.InteropServices;
using DevDriveBench.Engine.Model;
using Microsoft.Win32.SafeHandles;

namespace DevDriveBench.Engine.Platform;

/// <summary>
/// Enumerates fixed volumes and classifies them: system volume, Dev Drive (+ trusted) via the public
/// <c>FSCTL_QUERY_PERSISTENT_VOLUME_STATE</c>, and the backing disk (model, bus, VHD file) via the
/// Storage WMI v2 provider. All probes are read-only and best-effort.
/// </summary>
public static class DriveProbe
{
    // Public winioctl.h constants (ported from crutkas/devdrive PersistentVolumeState).
    private const uint DevVolumeFlag = 0x00002000;
    private const uint TrustedVolumeFlag = 0x00004000;
    private const uint FsctlQueryPersistentVolumeState = 0x9023C;
    private const uint QueryAllFlagsMask = 0xFFFFFFFF;

    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_FS_PERSISTENT_VOLUME_INFORMATION
    {
        public uint VolumeFlags;
        public uint FlagMask;
        public uint Version;
        public uint Reserved;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref FILE_FS_PERSISTENT_VOLUME_INFORMATION lpInBuffer, int nInBufferSize,
        ref FILE_FS_PERSISTENT_VOLUME_INFORMATION lpOutBuffer, int nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>The Windows system volume root, e.g. <c>C:\</c>.</summary>
    public static string SystemRoot =>
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

    /// <summary>All ready, fixed volumes with drive letters, system first, then Dev Drives, then the rest.</summary>
    public static IReadOnlyList<DriveDescriptor> GetFixedDrives()
    {
        string system = SystemRoot;
        var drives = new List<DriveDescriptor>();
        foreach (DriveInfo info in DriveInfo.GetDrives())
        {
            try
            {
                if (info.DriveType != DriveType.Fixed || !info.IsReady)
                {
                    continue;
                }

                uint? flags = QueryVolumeFlags(info.RootDirectory.FullName);
                (string? model, string? bus, string? backing) = QueryBackingDisk(info.Name[0]);
                drives.Add(new DriveDescriptor
                {
                    Root = info.RootDirectory.FullName,
                    Label = info.VolumeLabel ?? string.Empty,
                    FileSystem = info.DriveFormat,
                    IsSystem = string.Equals(info.RootDirectory.FullName, system, StringComparison.OrdinalIgnoreCase),
                    IsDevDrive = flags is uint f && (f & DevVolumeFlag) != 0,
                    IsTrusted = flags is uint t && (t & TrustedVolumeFlag) != 0,
                    SizeBytes = info.TotalSize,
                    FreeBytes = info.AvailableFreeSpace,
                    DiskModel = model,
                    BusType = bus,
                    BackingFile = backing,
                });
            }
            catch
            {
                // A volume that cannot be queried is simply not offered.
            }
        }

        return drives
            .OrderByDescending(d => d.IsSystem)
            .ThenByDescending(d => d.IsDevDrive)
            .ThenBy(d => d.Root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Raw persistent-volume flags for a root (<c>X:\</c>), or null when not queryable.</summary>
    public static uint? QueryVolumeFlags(string volumeRoot)
    {
        using SafeFileHandle handle = CreateFile(
            volumeRoot, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var input = new FILE_FS_PERSISTENT_VOLUME_INFORMATION { Version = 1, FlagMask = QueryAllFlagsMask };
        var output = default(FILE_FS_PERSISTENT_VOLUME_INFORMATION);
        int size = Marshal.SizeOf<FILE_FS_PERSISTENT_VOLUME_INFORMATION>();
        return DeviceIoControl(handle, FsctlQueryPersistentVolumeState, ref input, size, ref output, size, out _, IntPtr.Zero)
            ? output.VolumeFlags
            : null;
    }

    private static (string? Model, string? Bus, string? Backing) QueryBackingDisk(char letter)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            uint? diskNumber = null;
            using (var partitions = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{char.ToUpperInvariant(letter)}'")))
            {
                foreach (ManagementBaseObject p in partitions.Get())
                {
                    diskNumber = Convert.ToUInt32(p["DiskNumber"]);
                    break;
                }
            }

            if (diskNumber is null)
            {
                return (null, null, null);
            }

            using var disks = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT FriendlyName, BusType, Location FROM MSFT_Disk WHERE Number = {diskNumber}"));
            foreach (ManagementBaseObject d in disks.Get())
            {
                string? model = d["FriendlyName"] as string;
                string bus = BusTypeName(Convert.ToUInt16(d["BusType"]));
                string? location = d["Location"] as string;
                string? backing = bus == "File Backed Virtual" && !string.IsNullOrWhiteSpace(location) && location.Contains(':') ? location : null;
                return (model, bus, backing);
            }
        }
        catch
        {
            // WMI unavailable or access denied: the drive is still usable, just less described.
        }

        return (null, null, null);
    }

    private static string BusTypeName(ushort busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "File Backed Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown",
    };
}
