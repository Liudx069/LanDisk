using System.Runtime.InteropServices;
using System.Security.Principal;

// A benign extension payload for the automated extension tests.
//
// It does exactly what an approved extension command is allowed to do — echo its arguments and exit with a
// chosen code — and it can report its own elevation/integrity so the tests can prove the host ran it without
// administrator rights. It is never shipped with the product; it exists only as a real Windows PE for the
// extension execution path to run.

Console.WriteLine("ExtensionTestHelper started");

foreach (string arg in args) Console.WriteLine("arg: " + arg);

int exitCode = 0;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--exit" when i + 1 < args.Length && int.TryParse(args[i + 1], out int code):
            exitCode = code;
            i++;
            break;
        case "--sleep" when i + 1 < args.Length && int.TryParse(args[i + 1], out int ms):
            Thread.Sleep(ms);
            i++;
            break;
        case "--echo" when i + 1 < args.Length:
            Console.WriteLine("echo: " + args[i + 1]);
            i++;
            break;
        case "--whoami":
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                Console.WriteLine("name: " + identity.Name);
                Console.WriteLine("elevated: " + IsElevated());
                Console.WriteLine("integrity: " + Integrity());
            }
            break;
    }
}

Console.WriteLine("ExtensionTestHelper done");
return exitCode;

static string Integrity()
{
    // Query the token's own integrity label (S-1-16-xxxx) rather than scanning group memberships: the
    // mandatory label is a token property, not a group, which is why a low-integrity process does not simply
    // list it among its groups.
    if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */, out IntPtr token)) return "unknown";
    try
    {
        GetTokenInformation(token, 25 /* TokenIntegrityLevel */, IntPtr.Zero, 0, out int size);
        if (size <= 0) return "unknown";

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformationBuffer(token, 25, buffer, size, out _)) return "unknown";

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label; } — read the SID and its last sub-authority.
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero) return "unknown";

            if (!ConvertSidToStringSid(sid, out string? text) || text is null) return "unknown";
            int lastDash = text.LastIndexOf('-');
            if (lastDash < 0) return "unknown";
            if (!int.TryParse(text[(lastDash + 1)..], out int rid)) return "unknown";

            return rid switch
            {
                4096 => "low",
                8192 => "medium",
                12288 => "high",
                16384 => "system",
                _ => text
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
    finally
    {
        CloseHandle(token);
    }
}

[DllImport("kernel32.dll")]
static extern IntPtr GetCurrentProcess();

[DllImport("advapi32.dll", SetLastError = true)]
static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

[DllImport("advapi32.dll", SetLastError = true)]
static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

[DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
static extern bool GetTokenInformationBuffer(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool ConvertSidToStringSid(IntPtr sid, out string? stringSid);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool CloseHandle(IntPtr handle);

static bool IsElevated()
{
    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
    WindowsPrincipal principal = new(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}
