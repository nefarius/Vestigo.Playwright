using System.Text.Json;

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
    public static IEnumerable<string> Candidates(string? pathVariable, string programFiles, string? programFilesX86, string? localAppData, string? configuredExecutable = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
            yield return configuredExecutable;
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

/// <summary>
///     Vestigo gives the module <c>Vestigo:Modules:Playwright</c>. <c>ExecutablePath</c> is the managed host the plugin starts.
///     Settings are <c>appsettings.json</c> under <c>VESTIGO_SETTINGS_DIR</c>, or under ProgramData when that variable is unset.
/// </summary>
public static class PlaywrightHostSettings
{
    public static string File(string? settingsDirectoryOverride, string commonApplicationData)
    {
        string directory = string.IsNullOrWhiteSpace(settingsDirectoryOverride)
            ? Path.Combine(commonApplicationData, HostLayout.Company, HostLayout.Product)
            : settingsDirectoryOverride.Trim();
        return Path.Combine(directory, "appsettings.json");
    }

    public static string? ExecutablePath(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!TryGet(document.RootElement, "Vestigo", out JsonElement vestigo) ||
                !TryGet(vestigo, "Modules", out JsonElement modules) ||
                !TryGet(modules, "Playwright", out JsonElement playwright) ||
                !TryGet(playwright, "ExecutablePath", out JsonElement path) ||
                path.ValueKind != JsonValueKind.String)
                return null;
            string? value = path.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
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
