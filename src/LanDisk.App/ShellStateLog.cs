using System.Text;

using System.IO;

namespace LanDisk.App;

/// <summary>
/// Append-only record of what the shell decided about the server.
///
/// The window is a GUI: without this, "why did it say port 8891 was busy?" can only be answered by
/// looking at a screenshot. The file lives with the other logs under the data root and is read by
/// the acceptance script, so the shell's state is verifiable rather than only visible.
/// </summary>
internal sealed class ShellStateLog
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();

    public ShellStateLog(string dataRoot)
    {
        _path = System.IO.Path.Combine(dataRoot, "logs", "shell-state.log");
    }

    public string FilePath => _path;

    /// <summary>Best effort note for a message that has no <see cref="ServerStatus"/> behind it.</summary>
    public static void TryWriteLine(string dataRoot, string message)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            File.AppendAllText(Path.Combine(dataRoot, "logs", "shell-state.log"),
                $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is a convenience; a failure to write it must never take the shell down.
        }
    }

    public void Write(ServerStatus status)
    {
        var builder = new StringBuilder();
        builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
               .Append("  state=").Append(status.State)
               .Append("  version=").Append(status.Version.Length > 0 ? status.Version : "-")
               .Append("  commit=").Append(status.BuildCommit.Length > 0 ? status.BuildCommit : "-")
               .Append("  dataRoot=").Append(status.DataRoot.Length > 0 ? status.DataRoot : "-");

        if (status.ConflictedPort is int port) builder.Append("  conflictedPort=").Append(port);
        if (status.ErrorCode is not null) builder.Append("  code=").Append(status.ErrorCode);

        builder.Append("  bound=").Append(status.BoundUrls.Count == 0 ? "-" : string.Join(',', status.BoundUrls));

        if (!string.IsNullOrEmpty(status.Error))
            builder.Append("  message=").Append(status.Error.Replace('\n', ' ').Replace('\r', ' '));

        builder.AppendLine();

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                RotateIfNeeded();
                File.AppendAllText(_path, builder.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never break the thing they are describing.
            }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes) return;

            string previous = _path + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(_path, previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
