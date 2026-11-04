using System.Security.Principal;
using static PaceLaps.Agent.IRacing.Win32.NativeMethods;

namespace PaceLaps.Agent.IRacing.Win32;

/// <summary>
/// Windows drops keystrokes sent from a normal process to an elevated one (UIPI) without an error,
/// so the agent and iRacing must run at the same level.
/// </summary>
public static class ProcessElevation
{
    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>"yes", "no", or "unknown" (the token could not be read, which usually means the process is elevated and we are not).</summary>
    public static string DescribeProcessOfWindow(nint window)
    {
        GetWindowThreadProcessId(window, out var pid);
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
        {
            return "unknown";
        }

        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            {
                return "unknown";
            }

            try
            {
                return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(uint), out _)
                    ? (elevated != 0 ? "yes" : "no")
                    : "unknown";
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
