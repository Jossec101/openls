// Run-at-sign-in registration via the per-user Run key
// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run). No elevation, no
// shortcut/COM, and Task Manager > Startup apps lists and can disable it.
//
// The value stores this exe plus every flag given alongside --install-startup,
// so `openls --install-startup --capture dxgi --hotkey F8` registers exactly
// that command. --minimized is added when absent so the console does not
// land on top of the desktop at every sign-in.
using Microsoft.Win32;

namespace OpenLs;

public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "openls";

    public static int Install(string[] args, Action<string> log)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            log("--install-startup: cannot determine this executable's path.");
            return 8;
        }

        var keep = args.Where(a => a != "--install-startup" && a != "--uninstall-startup").ToList();
        if (!keep.Contains("--minimized")) keep.Add("--minimized");

        string cmd = Quote(exe) + (keep.Count > 0 ? " " + string.Join(" ", keep.Select(Quote)) : "");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException("CreateSubKey returned null");
            key.SetValue(ValueName, cmd, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            log("--install-startup: writing HKCU\\" + RunKey + " failed: " + ex.Message);
            return 8;
        }
        log("registered to run at sign-in (HKCU\\" + RunKey + "\\" + ValueName + "):");
        log("  " + cmd);
        log("remove with --uninstall-startup, or disable it under Task Manager > Startup apps.");
        return 0;
    }

    public static int Uninstall(Action<string> log)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) == null)
            {
                log("--uninstall-startup: no '" + ValueName + "' entry under HKCU\\" + RunKey + " - nothing to remove.");
                return 0;
            }
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            log("--uninstall-startup: " + ex.Message);
            return 8;
        }
        log("removed the '" + ValueName + "' run-at-sign-in entry.");
        return 0;
    }

    /// <summary>Current registration, or null when there is none.</summary>
    public static string? Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch { return null; }
    }

    private static string Quote(string a) =>
        a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
