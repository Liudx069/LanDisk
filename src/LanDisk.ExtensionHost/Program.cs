using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LanDisk.Core.Extensions;
using Microsoft.Win32.SafeHandles;

// LanDisk.ExtensionHost — the only process that ever runs extension code.
//
// The server never loads an extension binary. It writes a task description to a file and spawns this host,
// which is what actually launches the approved extension's entrypoint. Before it does, it puts the child in a
// Job Object with a process-count / memory / active-process ceiling, and it runs the child under a restricted
// token with Low Integrity so the extension has no more rights than a plain, non-elevated user process with
// its dangerous privileges disabled.
//
// If the restricted token or Low Integrity cannot be established on this machine, the host fails closed: it
// does not fall back to running the extension with its own (higher) rights, because that is exactly the
// privilege escalation this process exists to prevent.

internal static class Program
{
    private const string Usage = "LanDisk.ExtensionHost --task <file.json> | --probe";

    private static int Main(string[] args)
    {
        if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase)) return Probe();

        string? taskFile = Value(args, "--task");
        if (taskFile is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        return Run(taskFile);
    }

    // ---- sandbox -----------------------------------------------------------

    private static int Run(string taskFile)
    {
        HostResult result = new() { State = "Failed" };

        TaskSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<TaskSpec>(File.ReadAllText(taskFile), JsonOptions);
        }
        catch (Exception ex)
        {
            result.Error = "TASK_INVALID：" + ex.Message;
            WriteResult(taskFile, result);
            return 3;
        }

        if (spec is null || string.IsNullOrWhiteSpace(spec.Entrypoint) || !File.Exists(spec.Entrypoint))
        {
            result.Error = "ENTRYPOINT_MISSING：扩展入口不存在。";
            WriteResult(taskFile, result);
            return 3;
        }

        using var job = new SandboxJob(spec.TimeoutSeconds);
        IntPtr restricted = IntPtr.Zero;
        bool restrictedOk = false;
        bool lowIntegrityOk = false;

        try
        {
            restrictedOk = TryCreateRestrictedToken(out restricted);
            lowIntegrityOk = restrictedOk && TrySetLowIntegrity(restricted);
        }
        catch
        {
            restrictedOk = false;
            lowIntegrityOk = false;
        }

        // FAIL CLOSED: no restricted token means no run. Never launch with our own rights.
        if (!restrictedOk || !lowIntegrityOk)
        {
            result.Error = "RESTRICTED_TOKEN_UNAVAILABLE：本机无法建立受限令牌 / Low Integrity，扩展拒绝运行（不会退化为高权限执行）。";
            WriteResult(taskFile, result);
            return 4;
        }

        var psi = new ProcessStartInfo
        {
            FileName = spec.Entrypoint,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(spec.Entrypoint) ?? string.Empty
        };
        foreach (string argument in spec.Arguments ?? []) psi.ArgumentList.Add(argument);

        NativeChild child;
        try
        {
            // CreateProcessAsUser runs the child under the restricted token with redirected stdio handles.
            child = NativeChild.StartRestricted(psi, restricted);
        }
        catch (Exception ex)
        {
            result.Error = "START_FAILED：" + ex.GetType().Name;
            WriteResult(taskFile, result);
            return 5;
        }

        result.RestrictedToken = restrictedOk;
        result.LowIntegrity = lowIntegrityOk;
        result.Elevated = child.IsElevated;

        job.Assign(child.ProcessHandle);

        var stdout = new CappedOutputReader(child.Stdout, ExtensionLimits.MaxLogBytes);
        var stderr = new CappedOutputReader(child.Stderr, ExtensionLimits.MaxLogBytes);
        stdout.Start();
        stderr.Start();

        bool exited = child.WaitForExit(Math.Max(1, spec.TimeoutSeconds) * 1000);
        if (!exited)
        {
            job.Terminate();
            child.WaitForExit(5000);
            result.State = "TimedOut";
            result.Error = "TIMEOUT：命令超过 " + spec.TimeoutSeconds + " 秒，进程树已终止。";
        }
        else
        {
            int exitCode = child.ExitCode;
            result.State = exitCode == 0 ? "Succeeded" : "Failed";
            result.ExitCode = exitCode;
            if (exitCode != 0) result.Error = "EXTENSION_EXIT_" + exitCode;
        }

        stdout.Join();
        stderr.Join();
        result.Stdout = stdout.Text;
        result.Stderr = stderr.Text;

        WriteResult(taskFile, result);
        return 0;
    }

    private static int Probe()
    {
        bool restricted = TryCreateRestrictedToken(out IntPtr token);
        bool low = restricted && TrySetLowIntegrity(token);
        Console.WriteLine("restricted-token: " + restricted);
        Console.WriteLine("low-integrity: " + low);
        Console.WriteLine("current-elevated: " + CurrentElevated());
        Console.WriteLine("last-token-error: " + LastTokenError);
        if (token != IntPtr.Zero) CloseHandle(token);
        return 0;
    }

    private static string ArgumentListToString(ProcessStartInfo psi)
    {
        // The host builds the command line from ArgumentList, never from a user-supplied string; each argument
        // is quoted and escaped so a space or quote in an argument cannot break out into a second command.
        return string.Join(' ', psi.ArgumentList.Select(QuoteWindowsArg));
    }

    private static string QuoteWindowsArg(string arg)
    {
        if (arg.Length > 0 && !arg.Any(char.IsWhiteSpace) && !arg.Contains('"')) return arg;
        return '"' + arg.Replace("\"", "\\\"") + '"';
    }

    private static bool CurrentElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsTokenElevated(IntPtr token)
    {
        var elevation = new TOKEN_ELEVATION();
        int size = Marshal.SizeOf<TOKEN_ELEVATION>();
        if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, out elevation, size, out _)) return true;
        return elevation.TokenIsElevated != 0;
    }

    // ---- restricted token ---------------------------------------------------

    private const int DISABLE_MAX_PRIVILEGE = 0x1;

    /// <summary>The Win32 error from the last failed token attempt, reported by --probe for diagnosis.</summary>
    private static int LastTokenError { get; set; }
    private const int TOKEN_DUPLICATE = 0x0002;
    private const int TOKEN_QUERY = 0x0008;
    private const int TOKEN_ADJUST_DEFAULT = 0x0080;
    private const int TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const int TOKEN_ALL_ACCESS = 0x000F01FF;
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static bool TryCreateRestrictedToken(out IntPtr restrictedToken)
    {
        restrictedToken = IntPtr.Zero;

        IntPtr currentToken = IntPtr.Zero;
        IntPtr adminsSid = IntPtr.Zero;
        IntPtr sidsToDisable = IntPtr.Zero;
        try
        {
            // TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ASSIGN_PRIMARY — the access the
            // documented "launch a low-integrity process" recipe needs.
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ASSIGN_PRIMARY, out currentToken))
            {
                LastTokenError = Marshal.GetLastWin32Error();
                return false;
            }

            // The privileged part of this product can be started from an elevated shell. A restricted token
            // alone does not remove the Administrators group: it only disables privileges. To make the child
            // genuinely "not an administrator", the Administrators SID is disabled (deny-only) as well, so an
            // extension cannot regain administrative rights even when the operator launched LanDisk elevated.
            int disableCount = 0;
            if (ConvertStringSidToSid("S-1-5-32-544", out adminsSid))
            {
                var disabled = new SID_AND_ATTRIBUTES { Sid = adminsSid, Attributes = 0 };
                sidsToDisable = Marshal.AllocHGlobal(Marshal.SizeOf<SID_AND_ATTRIBUTES>());
                Marshal.StructureToPtr(disabled, sidsToDisable, false);
                disableCount = 1;
            }

            // DISABLE_MAX_PRIVILEGE removes every privilege this process can remove; the Low Integrity label is
            // applied separately through SetTokenInformation below, which is the supported order. Passing the
            // integrity SID through SidsToRestrict instead makes CreateRestrictedToken fail with
            // ERROR_INVALID_PARAMETER, so that list stays empty.
            bool ok = CreateRestrictedToken(
                currentToken,
                DISABLE_MAX_PRIVILEGE,
                disableCount, sidsToDisable,
                0, IntPtr.Zero,
                0, IntPtr.Zero,
                out restrictedToken);
            if (!ok) LastTokenError = Marshal.GetLastWin32Error();
            return ok;
        }
        finally
        {
            if (sidsToDisable != IntPtr.Zero) Marshal.FreeHGlobal(sidsToDisable);
            if (adminsSid != IntPtr.Zero) LocalFree(adminsSid);
            if (currentToken != IntPtr.Zero) CloseHandle(currentToken);
        }
    }

    private static bool TrySetLowIntegrity(IntPtr token)
    {
        IntPtr sid = IntPtr.Zero;
        IntPtr label = IntPtr.Zero;
        try
        {
            if (!ConvertStringSidToSid("S-1-16-4096", out sid)) return false;

            int labelSize = Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + 68;   // 68 = SECURITY_MAX_SID_SIZE
            label = Marshal.AllocHGlobal(labelSize);
            var mandatoryLabel = new TOKEN_MANDATORY_LABEL
            {
                Label = new SID_AND_ATTRIBUTES { Sid = sid, Attributes = 0x00000020 /* SE_GROUP_INTEGRITY */ }
            };
            Marshal.StructureToPtr(mandatoryLabel, label, false);

            return SetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, label, (uint)labelSize);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (label != IntPtr.Zero) Marshal.FreeHGlobal(label);
            if (sid != IntPtr.Zero) LocalFree(sid);
        }
    }

    // ---- result ------------------------------------------------------------

    private sealed class HostResult
    {
        public string State { get; set; } = "Failed";
        public int ExitCode { get; set; }
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
        public string? Error { get; set; }
        public bool RestrictedToken { get; set; }
        public bool LowIntegrity { get; set; }
        public bool Elevated { get; set; }
    }

    private sealed class TaskSpec
    {
        public string? Entrypoint { get; set; }
        public List<string>? Arguments { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static void WriteResult(string taskFile, HostResult result)
    {
        string resultFile = taskFile + ".result.json";
        File.WriteAllText(resultFile, JsonSerializer.Serialize(result, JsonOptions));
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // ---- P/Invoke -----------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateRestrictedToken(
        IntPtr existingTokenHandle,
        int flags,
        int disableSidCount,
        IntPtr sidsToDisable,
        int deletePrivilegeCount,
        IntPtr privilegesToDelete,
        int restrictedSidCount,
        IntPtr sidsToRestrict,
        out IntPtr newTokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr tokenHandle, TOKEN_INFORMATION_CLASS tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, TOKEN_INFORMATION_CLASS tokenInformationClass, out TOKEN_ELEVATION tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        int creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenElevation = 20,
        TokenIntegrityLevel = 25
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }
}

/// <summary>A job object with limits, so a timed-out or runaway extension dies with its whole process tree.</summary>
internal sealed class SandboxJob : IDisposable
{
    private readonly IntPtr _handle;

    public SandboxJob(int timeoutSeconds)
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject 失败");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                // Flags and their matching fields must agree, or SetInformationJobObject returns
                // ERROR_INVALID_PARAMETER: PROCESS_TIME uses PerProcessUserTimeLimit, JOB_TIME uses
                // PerJobUserTimeLimit, ACTIVE_PROCESS uses ActiveProcessLimit, PROCESS_MEMORY uses
                // ProcessMemoryLimit and KILL_ON_JOB_CLOSE just needs the flag.
                LimitFlags = 0x00000002 /* JOB_OBJECT_LIMIT_PROCESS_TIME */
                             | 0x00000004 /* JOB_OBJECT_LIMIT_JOB_TIME */
                             | 0x00000008 /* JOB_OBJECT_LIMIT_ACTIVE_PROCESS */
                             | 0x00000100 /* JOB_OBJECT_LIMIT_PROCESS_MEMORY */
                             | 0x00002000 /* JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE */,
                PerProcessUserTimeLimit = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)).Ticks,
                PerJobUserTimeLimit = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)).Ticks,
                ActiveProcessLimit = 4
            },
            ProcessMemoryLimit = (IntPtr)(256L * 1024 * 1024)
        };

        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(_handle, JobObjectInfoType.ExtendedLimitInformation, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject 失败");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Assign(IntPtr processHandle)
    {
        if (!AssignProcessToJobObject(_handle, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject 失败");
    }

    public void Terminate() => TerminateJobObject(_handle, 1);

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
    }

    private enum JobObjectInfoType { ExtendedLimitInformation = 9 }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public IntPtr ProcessMemoryLimit;
        public IntPtr JobMemoryLimit;
        public IntPtr PeakProcessMemoryUsed;
        public IntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, JobObjectInfoType infoType, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Reads a child's stdout/stderr up to a byte cap, so a runaway extension cannot fill the host's memory with
/// an unbounded log. Anything past the cap is dropped and the truncation is noted.
/// </summary>
internal sealed class CappedOutputReader
{
    private readonly StreamReader _reader;
    private readonly int _limit;
    private readonly StringBuilder _buffer = new();
    private Thread? _thread;
    private int _truncated;

    public CappedOutputReader(StreamReader reader, int limit)
    {
        _reader = reader;
        _limit = limit;
    }

    public void Start()
    {
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "extension-output" };
        _thread.Start();
    }

    private void ReadLoop()
    {
        try
        {
            var chunk = new char[4096];
            int read;
            while ((read = _reader.Read(chunk, 0, chunk.Length)) > 0)
            {
                int remaining = _limit - _buffer.Length;
                if (remaining <= 0)
                {
                    _truncated += read;
                    continue;
                }
                _buffer.Append(chunk, 0, Math.Min(read, remaining));
                if (read > remaining) _truncated += read - remaining;
            }
        }
        catch
        {
        }
    }

    public void Join()
    {
        try { _thread?.Join(5000); } catch { }
        try { _reader.Dispose(); } catch { }
    }

    public string Text
        => _truncated > 0 ? _buffer + $"\n…（截断 {_truncated} 字符）" : _buffer.ToString();
}

/// <summary>
/// A child process launched with <c>CreateProcessAsUser</c> under a restricted token, with stdout/stderr
/// redirected through inherited pipe handles so the host can capture them.
/// </summary>
internal sealed class NativeChild
{
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int HANDLE_FLAG_INHERIT = 0x00000001;
    private const uint INFINITE = 0xFFFFFFFF;

    private NativeChild(IntPtr process, IntPtr thread, StreamReader stdout, StreamReader stderr, bool elevated)
    {
        ProcessHandle = process;
        _thread = thread;
        Stdout = stdout;
        Stderr = stderr;
        IsElevated = elevated;
    }

    public IntPtr ProcessHandle { get; }
    private readonly IntPtr _thread;
    public StreamReader Stdout { get; }
    public StreamReader Stderr { get; }
    public bool IsElevated { get; }

    public static NativeChild StartRestricted(ProcessStartInfo psi, IntPtr token)
    {
        CreatePipe(out IntPtr readOut, out IntPtr writeOut, IntPtr.Zero, 0);
        CreatePipe(out IntPtr readErr, out IntPtr writeErr, IntPtr.Zero, 0);

        // The pipe ends the *child* writes to must be inheritable; the ends the parent reads from must not be.
        // CreatePipe with NULL security attributes produces non-inheritable handles, so this is stated
        // explicitly rather than assumed — without it the child's output never reaches the parent.
        SetHandleInformation(writeOut, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
        SetHandleInformation(writeErr, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
        SetHandleInformation(readOut, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(readErr, HANDLE_FLAG_INHERIT, 0);

        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESTDHANDLES,
            hStdOutput = writeOut,
            hStdError = writeErr,
            hStdInput = IntPtr.Zero
        };

        string commandLine = psi.FileName + " " + string.Join(' ', psi.ArgumentList.Select(Quote));
        bool created = CreateProcessAsUser(
            token, null, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0, IntPtr.Zero,
            psi.WorkingDirectory, ref startupInfo, out PROCESS_INFORMATION processInfo);

        CloseHandle(writeOut);
        CloseHandle(writeErr);

        if (!created)
        {
            CloseHandle(readOut);
            CloseHandle(readErr);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser 失败");
        }

        bool elevated = CanUseAdministratorRights(processInfo.hProcess);
        return new NativeChild(
            processInfo.hProcess, processInfo.hThread,
            new StreamReader(new FileStream(new SafeFileHandle(readOut, ownsHandle: true), FileAccess.Read, 4096, false)),
            new StreamReader(new FileStream(new SafeFileHandle(readErr, ownsHandle: true), FileAccess.Read, 4096, false)),
            elevated);
    }

    public bool WaitForExit(int milliseconds)
        => WaitForSingleObject(ProcessHandle, (uint)milliseconds) == 0 /* WAIT_OBJECT_0 */;

    public int ExitCode
    {
        get
        {
            GetExitCodeProcess(ProcessHandle, out uint code);
            return (int)code;
        }
    }

    private static string Quote(string arg)
        => arg.Length > 0 && !arg.Any(char.IsWhiteSpace) && !arg.Contains('"') ? arg : '"' + arg.Replace("\"", "\\\"") + '"';

    /// <summary>
    /// True only when the child token can actually use administrator rights, i.e. its Administrators group is
    /// present and enabled.
    ///
    /// <para>This deliberately does not use <c>TokenElevation</c>: a restricted token derived from an elevated
    /// process still reports "elevated" even after the Administrators SID has been disabled, which would make
    /// the report say the opposite of the truth. The enabled-Administrators check is the same question
    /// <c>IsInRole(Administrator)</c> answers inside the child.</para>
    /// </summary>
    private static bool CanUseAdministratorRights(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out IntPtr token)) return true;
        try
        {
            GetTokenInformationBuffer(token, 8 /* TokenGroups */, IntPtr.Zero, 0, out int size);
            if (size <= 0) return true;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformationBuffer(token, 8, buffer, size, out _)) return true;

                int count = Marshal.ReadInt32(buffer);
                // TOKEN_GROUPS: DWORD GroupCount, then SID_AND_ATTRIBUTES[] (8-byte aligned on x64).
                IntPtr groups = IntPtr.Add(buffer, 8);
                int entrySize = Marshal.SizeOf<SID_AND_ATTRIBUTES>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = IntPtr.Add(groups, i * entrySize);
                    IntPtr sid = Marshal.ReadIntPtr(entry);
                    uint attributes = (uint)Marshal.ReadInt32(entry, IntPtr.Size);
                    if (sid == IntPtr.Zero) continue;
                    if (!ConvertSidToStringSid(sid, out string? text) || text is null) continue;
                    if (string.Equals(text, "S-1-5-32-544", StringComparison.Ordinal) && (attributes & 0x4 /* SE_GROUP_ENABLED */) != 0)
                        return true;
                }

                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return true;
        }
        finally { CloseHandle(token); }
    }

    private enum TOKEN_INFORMATION_CLASS { TokenElevation = 20 }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION { public int TokenIsElevated; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr readHandle, out IntPtr writeHandle, IntPtr securityAttributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token, string? applicationName, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, int creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, TOKEN_INFORMATION_CLASS tokenInformationClass, out TOKEN_ELEVATION tokenInformation, int tokenInformationLength, out int returnLength);

    /// <summary>Variable-length token information (TokenGroups), read into a caller-allocated buffer.</summary>
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static extern bool GetTokenInformationBuffer(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out string? stringSid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
