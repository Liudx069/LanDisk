using LanDisk.Core.Configuration;
using LanDisk.Core.Interop;
using LanDisk.Core.Paths;

namespace LanDisk.Core.Storage;

/// <summary>
/// Where LanDisk keeps staging and recycle payloads for one volume.
/// Payloads live in a dedicated internal directory at the volume root so that moving a file
/// into the recycle bin stays a same-volume metadata operation instead of a copy.
/// When that directory cannot be created (read-only media, missing permission), LanDisk falls
/// back to a per-volume directory under the application data root and marks the store as
/// cross-volume, which forces the journalled copy path.
/// </summary>
public sealed class VolumeStore
{
    public required string VolumeGuidPath { get; init; }
    public required string MountPoint { get; init; }
    public required string RootPath { get; init; }
    public required bool IsCrossVolume { get; init; }
    public string? FallbackDetail { get; init; }

    public string StagingDir => System.IO.Path.Combine(RootPath, "staging");
    public string RecycleDir => System.IO.Path.Combine(RootPath, "recycle");

    public void EnsureLayout()
    {
        Directory.CreateDirectory(StagingDir);
        Directory.CreateDirectory(RecycleDir);
    }

    public bool TryGetFreeSpace(out ulong freeForCaller, out ulong total, out ulong totalFree)
    {
        string probe = IsCrossVolume ? RootPath : MountPoint;
        return Win32FileSystem.TryGetVolumeSpace(probe, out freeForCaller, out total, out totalFree);
    }
}

public sealed class VolumeStoreLocator
{
    private readonly AppPaths _paths;

    public VolumeStoreLocator(AppPaths paths) => _paths = paths;

    /// <summary>
    /// Forces the app-data (cross-volume) store even when the volume root is writable.
    /// Used only by the automated test suite to exercise the journalled copy path without
    /// having to make a real disk read-only.
    /// </summary>
    public bool ForceFallback { get; set; }

    /// <summary>
    /// Resolves (and lazily creates) the store for the volume that hosts <paramref name="path"/>.
    /// </summary>
    public VolumeStore Locate(string path)
    {
        string mountPoint = Win32FileSystem.GetVolumeMountPoint(path);
        string volumeGuid = Win32FileSystem.GetVolumeGuidPath(mountPoint);

        string primary = AppPaths.VolumeStoreRoot(mountPoint);
        string? fallbackDetail = null;

        if (!ForceFallback)
        {
            try
            {
                if (AppPaths.VolumeStoreName.Equals(System.IO.Path.GetFileName(primary), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(primary);
                    var store = new VolumeStore
                    {
                        VolumeGuidPath = volumeGuid,
                        MountPoint = mountPoint,
                        RootPath = primary,
                        IsCrossVolume = false
                    };
                    store.EnsureLayout();
                    return store;
                }
            }
            catch (Exception ex)
            {
                fallbackDetail = $"无法在卷根目录创建 {AppPaths.VolumeStoreName}：{ex.Message}";
            }
        }
        else
        {
            fallbackDetail = "测试模式：强制使用应用数据目录作为存储位置。";
        }

        string safeName = SecretTokensHash(volumeGuid);
        string fallbackRoot = System.IO.Path.Combine(_paths.FallbackStoreDir, safeName);
        Directory.CreateDirectory(fallbackRoot);
        var fallbackStore = new VolumeStore
        {
            VolumeGuidPath = volumeGuid,
            MountPoint = mountPoint,
            RootPath = fallbackRoot,
            IsCrossVolume = true,
            FallbackDetail = fallbackDetail ?? "卷根目录不可写，使用应用数据目录作为暂存与回收存储。"
        };
        fallbackStore.EnsureLayout();
        return fallbackStore;
    }

    private static string SecretTokensHash(string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
    }

    /// <summary>
    /// Finds the current mount point for a volume GUID path by walking the mounted drives.
    /// Drive letters are explicitly not treated as stable identity, so this lookup is how a
    /// recycle entry finds its payload again after the user reconnects a removable disk.
    /// </summary>
    public static string? FindMountPointForVolume(string volumeGuidPath)
    {
        if (string.IsNullOrEmpty(volumeGuidPath)) return null;

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return null; }

        foreach (DriveInfo drive in drives)
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
                string root = drive.RootDirectory.FullName;
                string guid = Win32FileSystem.GetVolumeGuidPath(root);
                if (string.Equals(guid, volumeGuidPath, StringComparison.OrdinalIgnoreCase))
                    return root;
            }
            catch { }
        }

        return null;
    }

    /// <summary>True when the stored path is currently reachable through its original volume.</summary>
    public static bool IsStoreReachable(VolumeStore store)
    {
        try
        {
            if (!Directory.Exists(store.MountPoint)) return false;
            string current = Win32FileSystem.GetVolumeGuidPath(store.MountPoint);
            return string.Equals(current, store.VolumeGuidPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
