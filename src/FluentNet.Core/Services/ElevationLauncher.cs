using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace FluentNet.Core.Services;

public static class ElevationLauncher
{
    public static bool IsProcessElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static int RunElevatedCli(string cliExecutablePath, string arguments, bool hideWindow = true)
    {
        if (!File.Exists(cliExecutablePath))
        {
            throw new FileNotFoundException("Elevated worker FluentNet.Cli.exe not found.", cliExecutablePath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = cliExecutablePath,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = hideWindow ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            return -1;
        }

        proc.WaitForExit();
        return proc.ExitCode;
    }

    public static string? ResolveCliPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "FluentNet.Cli.exe"),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\FluentNet.Cli\bin\x64\Release\net10.0-windows10.0.19041.0\FluentNet.Cli.exe")),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\FluentNet.Cli\bin\x64\Release\net10.0-windows10.0.19041.0\FluentNet.Cli.exe")),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\FluentNet.Cli\bin\x64\Release\net10.0-windows10.0.19041.0\FluentNet.Cli.exe"))
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
