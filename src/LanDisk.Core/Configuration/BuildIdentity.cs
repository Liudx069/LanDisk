using System.Reflection;

namespace LanDisk.Core.Configuration;

/// <summary>
/// Identity of the build that is running.
///
/// The values are stamped into the assembly at build time (see <c>Directory.Build.props</c>) from the
/// command line rather than being discovered by the compiler, so the number reported by a running
/// product is exactly the commit the release pipeline built — which is what makes
/// "release buildCommit == update manifest buildCommit == final HEAD" checkable instead of asserted.
///
/// A development build deliberately reports an empty commit: it must not be able to pass a candidate
/// identity check by accident.
/// </summary>
public static class BuildIdentity
{
    private static readonly Lazy<(string Commit, string BuildId, string AssemblyVersion, string BuiltAtUtc)> Cached =
        new(Read, isThreadSafe: true);

    /// <summary>Exact git commit this assembly was built from, or an empty string for a dev build.</summary>
    public static string Commit => Cached.Value.Commit;

    /// <summary>Free-form build identifier (configuration, runtime identifier, …).</summary>
    public static string BuildId => Cached.Value.BuildId;

    public static string AssemblyVersion => Cached.Value.AssemblyVersion;

    /// <summary>Build time in UTC, taken from the assembly file timestamp.</summary>
    public static string BuiltAtUtc => Cached.Value.BuiltAtUtc;

    /// <summary>True when this build carries a commit, i.e. it can be compared against a git HEAD.</summary>
    public static bool HasCommit => Commit.Length > 0;

    private static (string, string, string, string) Read()
    {
        Assembly assembly = typeof(BuildIdentity).Assembly;

        string commit = Metadata(assembly, "LanDiskBuildCommit");
        string buildId = Metadata(assembly, "LanDiskBuildId");

        // The product version is stamped by Directory.Build.props from the project's $(Version).
        // Falling back to the assembly version keeps a plain `dotnet build` usable, but the stamped
        // value is what the release pipeline reports.
        string version = Metadata(assembly, "LanDiskProductVersion");
        if (version.Length == 0) version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        string builtAt;
        try
        {
            string? location = assembly.Location;
            builtAt = !string.IsNullOrEmpty(location) && File.Exists(location)
                ? File.GetLastWriteTimeUtc(location).ToString("u")
                : DateTimeOffset.UtcNow.ToString("u");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            builtAt = DateTimeOffset.UtcNow.ToString("u");
        }

        return (commit, buildId, version, builtAt);
    }

    private static string Metadata(Assembly assembly, string key)
    {
        try
        {
            foreach (AssemblyMetadataAttribute attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(attribute.Key, key, StringComparison.Ordinal)) return attribute.Value ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Reflection over attributes can fail in trimmed or single-file scenarios; an empty
            // commit is the honest answer there.
        }

        return string.Empty;
    }
}
