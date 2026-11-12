using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Vestigo.Playwright;

internal interface IClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal interface IDeploymentProbe
{
    bool IsInteractiveDesktop { get; }
    bool IsBrowserInstalled(string browser);
    bool FileExists(string path);
}

internal sealed class WindowsDeploymentProbe : IDeploymentProbe
{
    public bool IsInteractiveDesktop => DesktopSession.IsInteractiveDesktop();
    public bool IsBrowserInstalled(string browser) => BrowserDiscovery.IsInstalled(browser);
    public bool FileExists(string path) => File.Exists(path);
}

internal static class DesktopSession
{
    public static bool IsInteractiveDesktop()
    {
        if (!OperatingSystem.IsWindows())
            return Environment.UserInteractive;
        if (!NativeSession.ProcessIdToSessionId((uint)Environment.ProcessId, out uint sessionId))
            return false;
        // Session 0 is the isolated service session and has no logged-on user desktop.
        return sessionId != 0;
    }
}

internal static class NativeSession
{
    // ProcessIdToSessionId is a one-call kernel32 query. CsWin32 is not referenced by this plugin.
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);
}

internal static class BrowserDiscovery
{
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    public static bool IsInstalled(string browser)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        string executable = browser switch
        {
            "chrome" => "chrome.exe",
            "msedge" => "msedge.exe",
            _ => ""
        };
        if (executable.Length == 0)
            return false;
        string appPaths = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}";
        return ReadAppPath(Microsoft.Win32.Registry.LocalMachine, appPaths) ||
               ReadAppPath(Microsoft.Win32.Registry.CurrentUser, appPaths);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool ReadAppPath(Microsoft.Win32.RegistryKey hive, string subkey)
    {
        using Microsoft.Win32.RegistryKey? key = hive.OpenSubKey(subkey);
        return key?.GetValue(null) is string path && path.Length > 0 && File.Exists(path);
    }
}

internal static class DirectoryProtection
{
    public static void CreateProtected(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        DirectorySecurity security = DesiredSecurity();
        if (!Directory.Exists(path))
            security.CreateDirectory(path);
        ValidateExisting(path);
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity DesiredSecurity()
    {
        SecurityIdentifier? current = WindowsIdentity.GetCurrent().User;
        if (current is null)
            throw new PlaywrightException(PlaywrightCodes.Configuration, "The current Windows identity has no user SID.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier sid in Trusted(current))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateExisting(string path)
    {
        SecurityIdentifier? current = WindowsIdentity.GetCurrent().User;
        if (current is null)
            throw new PlaywrightException(PlaywrightCodes.Configuration, "The current Windows identity has no user SID.");
        DirectorySecurity security = new DirectoryInfo(path).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner, current))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "The Playwright state directory owner is not trusted.");
        if (!security.AreAccessRulesProtected)
            throw new PlaywrightException(PlaywrightCodes.Configuration, "The Playwright state directory must not inherit write access.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid || IsTrusted(sid, current))
                continue;
            const FileSystemRights dangerous = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles;
            if ((rule.FileSystemRights & dangerous) != 0)
                throw new PlaywrightException(PlaywrightCodes.Configuration, "The Playwright state directory allows an untrusted account to change it.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<SecurityIdentifier> Trusted(SecurityIdentifier current)
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        yield return current;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTrusted(SecurityIdentifier sid, SecurityIdentifier current) =>
        sid.Equals(current) ||
        sid.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) ||
        sid.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
}

internal static class RuntimeChecks
{
    public static void EnsureRunnable(PlaywrightSettings settings, IDeploymentProbe probe)
    {
        if (settings.Mode != BackendMode.Managed)
            return;
        if (settings.Headed && !probe.IsInteractiveDesktop)
            throw new PlaywrightException(
                PlaywrightCodes.Configuration,
                "Managed headed mode cannot show a browser from a Windows service session. Run Playwright MCP as the logged-on user and set Mode to External, or set Headed to false.");
        if (string.IsNullOrWhiteSpace(settings.ExecutablePath) || !probe.FileExists(settings.ExecutablePath))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "The managed Playwright executable was not found.");
        if (!probe.IsBrowserInstalled(settings.Browser))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "The selected Chrome or Edge installation was not found.");
    }
}
