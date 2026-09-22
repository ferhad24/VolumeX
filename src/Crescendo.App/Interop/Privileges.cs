using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Crescendo.Interop;

/// <summary>
/// Enables token privileges that an elevated process holds but that Windows
/// leaves disabled by default.
/// </summary>
/// <remarks>
/// Needed because the MMDevices registry tree is owned by TrustedInstaller.
/// Administrators may write values there but may not always create the
/// <c>FxProperties</c> subkey, so Crescendo has to take ownership of that one
/// key on drivers that ship without it.
/// </remarks>
internal static class Privileges
{
    public const string TakeOwnership = "SeTakeOwnershipPrivilege";
    public const string Restore = "SeRestorePrivilege";
    public const string Backup = "SeBackupPrivilege";

    public static bool TryEnable(string privilegeName)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            return false;

        try
        {
            if (!LookupPrivilegeValueW(null, privilegeName, out LUID luid))
                return false;

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            // AdjustTokenPrivileges reports success even when it silently skipped
            // a privilege the token does not hold.
            return Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public static void EnableOrThrow(string privilegeName)
    {
        if (!TryEnable(privilegeName))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not enable {privilegeName}.");
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
