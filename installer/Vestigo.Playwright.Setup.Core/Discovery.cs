namespace Vestigo.Playwright.Setup;

public static class HostLayout
{
    public const string Company = "Nefarius Software Solutions";
    public const string Product = "Vestigo";

    public static string DefaultDirectory(string programFiles) =>
        Path.Combine(programFiles, Company, Product);

    public static bool ContainsHost(Func<string, bool> fileExists, string directory) =>
        fileExists(Path.Combine(directory, "Vestigo.exe")) &&
        fileExists(Path.Combine(directory, "Vestigo.Abstractions.dll"));
}

public static class NodeLayout
{
    public static IEnumerable<string> Candidates(string? pathVariable, string programFiles, string? programFilesX86, string? localAppData)
    {
        foreach (string entry in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Path.Combine(entry, "node.exe");
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, "nodejs", "node.exe");
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return Path.Combine(programFilesX86, "nodejs", "node.exe");
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "Programs", "nodejs", "node.exe");
    }

    public static bool Found(Func<string, bool> fileExists, IEnumerable<string> candidates) =>
        candidates.Any(fileExists);
}

public static class BrowserLayout
{
    public static readonly string[] AppPathKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"
    ];

    public static bool Found(Func<string, string, string?> readDefaultValue, Func<string, bool> fileExists)
    {
        foreach (string hive in new[] { "HKLM", "HKCU" })
        {
            foreach (string key in AppPathKeys)
            {
                string? path = readDefaultValue(hive, key);
                if (!string.IsNullOrWhiteSpace(path) && fileExists(path))
                    return true;
            }
        }

        return false;
    }
}
