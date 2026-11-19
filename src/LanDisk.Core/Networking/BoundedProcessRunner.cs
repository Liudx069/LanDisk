using System.Diagnostics;

namespace LanDisk.Core.Networking;

/// <summary>What one bounded child-process run produced.</summary>
/// <param name="Completed">
/// True only when both output streams were read to their end <b>and</b> the process exited within the budget.
/// When it is false, <see cref="StdOut"/> must not be treated as a complete listing.
/// </param>
public sealed record ProcessRunResult(bool Completed, int ExitCode, string StdOut, string StdErr, string Failure)
{
    public static ProcessRunResult Failed(string reason) => new(false, -1, string.Empty, string.Empty, reason);
}

/// <summary>
/// Runs a short-lived helper process with one overall time budget covering <b>reading both streams and the
/// process exiting</b>.
///
/// <para><b>Why this exists.</b> The firewall self-check originally did
/// <c>ReadToEnd(stdout)</c> → <c>ReadToEnd(stderr)</c> → <c>WaitForExit(15000)</c>. That has two defects that
/// the timeout cannot rescue:</para>
/// <list type="bullet">
///   <item><description><c>ReadToEnd</c> has no timeout of its own. If the child never closes stdout, the
///   first call never returns and the <c>WaitForExit</c> below it is never reached — the caller waits for
///   ever, on the UI thread.</description></item>
///   <item><description>Reading the streams one after the other can deadlock: the child blocks writing to
///   stderr once its pipe buffer fills, while we block reading stdout.</description></item>
/// </list>
///
/// <para>Both streams are therefore consumed concurrently, together with the exit, under a single deadline.
/// On expiry only the process this call created is terminated, and the wait for it is bounded too — a
/// cleanup path that can itself hang is not a cleanup path.</para>
/// </summary>
public static class BoundedProcessRunner
{
    /// <summary>Budget for waiting for a killed process to actually go away.</summary>
    private const int KillWaitMilliseconds = 3000;

    public static ProcessRunResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutMilliseconds,
        bool emptyOutputIsFailure = true)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        Process? process = null;
        try
        {
            process = Process.Start(info);
            if (process is null) return ProcessRunResult.Failed(fileName + " 无法启动");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task all = Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());

            if (!all.Wait(timeoutMilliseconds))
            {
                KillOnlyThisProcess(process);
                return ProcessRunResult.Failed(fileName + " 查询超时（" + (timeoutMilliseconds / 1000) + " 秒）");
            }

            // all completing means both streams reached their end and the process exited.
            if (stdoutTask.IsFaulted || stderrTask.IsFaulted)
                return ProcessRunResult.Failed(fileName + " 输出读取失败");

            string stdout = stdoutTask.Result;
            string stderr = stderrTask.Result;

            if (process.ExitCode != 0)
            {
                return new ProcessRunResult(false, process.ExitCode, stdout, stderr,
                    fileName + " 退出码 " + process.ExitCode +
                    (stderr.Trim().Length > 0 ? "（" + stderr.Trim() + "）" : string.Empty));
            }

            if (emptyOutputIsFailure && stdout.Trim().Length == 0)
                return new ProcessRunResult(false, process.ExitCode, stdout, stderr, fileName + " 没有返回任何内容");

            return new ProcessRunResult(true, process.ExitCode, stdout, stderr, string.Empty);
        }
        catch (Exception ex)
        {
            if (process is not null) KillOnlyThisProcess(process);
            return ProcessRunResult.Failed(fileName + " 执行失败：" + ex.GetType().Name);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Terminates only the process this method started, and waits for it in bounded time.
    ///
    /// <para>Deliberately <b>not</b> <c>Kill(entireProcessTree: true)</c>: the requirement is to end the query
    /// process we created and nothing else. <c>netsh</c> has no children, and a tree kill after a PID has been
    /// recycled could hit an unrelated process.</para>
    /// </summary>
    private static void KillOnlyThisProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
        try { process.WaitForExit(KillWaitMilliseconds); } catch (Exception) { }
    }
}
