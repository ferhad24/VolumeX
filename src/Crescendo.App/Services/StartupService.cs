using System.Diagnostics;
using System.IO;
using System.Text;

namespace Crescendo.Services;

/// <summary>
/// Starts Crescendo with Windows.
/// </summary>
/// <remarks>
/// A scheduled task is used instead of a Run registry entry because Crescendo
/// requires elevation: a Run entry would raise a UAC prompt on every sign-in,
/// while a task registered to run with highest privileges starts silently.
/// </remarks>
public static class StartupService
{
    private const string TaskName = "VolumeX";

    // The task 1.1.x created under the old name, pointing at Crescendo.exe.
    private const string LegacyTaskName = "Crescendo Audio Engine";

    public static bool IsEnabled() => TaskExists(TaskName);

    public static bool SetEnabled(bool enabled, bool startMinimized)
    {
        return enabled ? Install(startMinimized) : Remove(TaskName) & Remove(LegacyTaskName);
    }

    /// <summary>
    /// After the rename: a startup task left by Crescendo points at an exe that
    /// no longer exists. Replace it with one for this executable, so "start
    /// with Windows" survives the upgrade instead of silently stopping.
    /// </summary>
    public static void MigrateLegacyTask(bool startMinimized)
    {
        if (!TaskExists(LegacyTaskName)) return;
        Remove(LegacyTaskName);
        Install(startMinimized);
    }

    private static bool TaskExists(string name)
    {
        try
        {
            using Process? process = Start("schtasks.exe", $"/Query /TN \"{name}\"");
            if (process is null) return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Install(bool startMinimized)
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "VolumeX.exe");
        string xmlPath = Path.Combine(Path.GetTempPath(), "volumex-task.xml");

        string arguments = startMinimized ? "--minimized" : string.Empty;

        // Registering from XML rather than the /Create shorthand is what makes
        // "run with highest privileges" and "start on logon of any user"
        // expressible in one shot.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts VolumeX and its tray icon at sign-in.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <GroupId>S-1-5-32-545</GroupId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{exe}"</Command>
                  <Arguments>{arguments}</Arguments>
                  <WorkingDirectory>{Path.GetDirectoryName(exe)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        try
        {
            File.WriteAllText(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            using Process? process = Start("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (process is null) return false;
            process.WaitForExit(10000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch (Exception) { }
        }
    }

    /// <summary>True when the task is gone afterwards, including if it never existed.</summary>
    private static bool Remove(string name)
    {
        if (!TaskExists(name)) return true;
        try
        {
            using Process? process = Start("schtasks.exe", $"/Delete /TN \"{name}\" /F");
            if (process is null) return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Process? Start(string fileName, string arguments) =>
        Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
}
