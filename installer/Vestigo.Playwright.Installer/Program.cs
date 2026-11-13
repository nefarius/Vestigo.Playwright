using System.Security.Cryptography;
using System.Text.Json;
using Vestigo.Playwright.Setup;
using WixSharp;
using WixSharp.CommonTasks;
using File = System.IO.File;

namespace Vestigo.Playwright.Installer;

internal static class Program
{
    private static readonly string[] RequiredPayload =
    [
        "plugin.json",
        "Vestigo.Playwright.dll",
        "Vestigo.Playwright.deps.json"
    ];

    private static readonly string[] SharedHostAssemblies =
    [
        "Vestigo.Abstractions.dll",
        "ModelContextProtocol.dll",
        "ModelContextProtocol.Core.dll",
        "ModelContextProtocol.AspNetCore.dll",
        "Microsoft.Extensions.AI.Abstractions.dll"
    ];

    public static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException("The MSI builder requires Windows and WiX 5.0.2.");
            BuildOptions options = BuildOptions.Parse(args);
            ValidatePayload(options.PayloadDirectory);
            string msi = Build(options);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(msi))).ToLowerInvariant();
            string manifestPath = Path.ChangeExtension(msi, ".manifest.json");
            var manifest = new
            {
                fileName = Path.GetFileName(msi),
                sha256 = hash,
                productVersion = options.Version.ToString(),
                productCode = ProductIdentity.ProductCode(options.Version).ToString("D"),
                upgradeCode = ProductIdentity.UpgradeCode.ToString("D")
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
            Console.WriteLine(msi);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string Build(BuildOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        string payloadMask = Path.Combine(options.PayloadDirectory, "*.*");
        var directory = new Dir(
            new Id("PLAYWRIGHTDIR"),
            @"%CommonAppDataFolder%\Nefarius Software Solutions\Vestigo\plugins\playwright",
            new Files(payloadMask));
        var project = new Project("Vestigo Playwright", directory)
        {
            GUID = ProductIdentity.UpgradeCode,
            UpgradeCode = ProductIdentity.UpgradeCode,
            ProductId = ProductIdentity.ProductCode(options.Version),
            Platform = Platform.x64,
            Scope = InstallScope.perMachine,
            UI = WUI.WixUI_Minimal,
            LicenceFile = Path.Combine(AppContext.BaseDirectory, "License.rtf"),
            Version = options.Version,
            OutDir = options.OutputDirectory,
            OutFileName = options.FileName,
            RebootSupressing = RebootSupressing.ReallySuppress
        };
        project.ControlPanelInfo.Manufacturer = "Nefarius Software Solutions";
        project.ControlPanelInfo.Comments = "Vestigo Playwright plugin files. Loading and the Playwright module stay administrator settings.";
        project.MajorUpgrade = new MajorUpgrade
        {
            Schedule = UpgradeSchedule.afterInstallInitialize,
            DowngradeErrorMessage = "A later version of Vestigo Playwright is already installed."
        };
        WixExtension.UI.PreferredVersion = "5.0.2";
        WixExtension.Util.PreferredVersion = "5.0.2";
        project.Include(WixExtension.Util);
        // Deferred so it runs after InstallFiles has created the directory. (OI)(CI) on a file is
        // inherit-only and blocks the next upgrade, so files get an explicit grant first and the
        // directory then gets the inheritable grant.
        const string pluginDirectory = "\"[CommonAppDataFolder]Nefarius Software Solutions\\Vestigo\\plugins\\playwright\"";
        project.AddAction(AclAction(
            "ProtectPlaywrightFiles",
            pluginDirectory + " /inheritance:r /grant:r SYSTEM:F Administrators:F Users:RX /T",
            Step.InstallFiles));
        project.AddAction(AclAction(
            "ProtectPlaywrightDirectory",
            pluginDirectory + " /inheritance:r /grant:r \"SYSTEM:(OI)(CI)F\" \"Administrators:(OI)(CI)F\" \"Users:(OI)(CI)RX\"",
            new Step("ProtectPlaywrightFiles")));
        string msi = project.BuildMsi();
        if (!File.Exists(msi))
            throw new InvalidOperationException("WiX did not produce the MSI.");
        return msi;
    }

    private static WixQuietExecAction AclAction(string id, string arguments, Step after)
    {
        var action = new WixQuietExecAction(
            new Id(id),
            "icacls.exe",
            arguments,
            Return.check,
            When.After,
            after,
            new Condition("NOT REMOVE~=\"ALL\""));
        action.Execute = Execute.deferred;
        action.Impersonate = false;
        return action;
    }

    private static void ValidatePayload(string payload)
    {
        if (!Directory.Exists(payload))
            throw new DirectoryNotFoundException("The plugin payload directory does not exist.");
        foreach (string required in RequiredPayload)
        {
            if (!File.Exists(Path.Combine(payload, required)))
                throw new InvalidOperationException($"The plugin payload is missing {required}.");
        }

        foreach (string path in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(path);
            if (SharedHostAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The plugin payload still contains the shared host assembly {name}.");
        }
    }
}

internal sealed record BuildOptions(string PayloadDirectory, Version Version, string OutputDirectory, string FileName)
{
    public static BuildOptions Parse(string[] args)
    {
        string? payload = null;
        string? version = null;
        string? output = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--payload" when index + 1 < args.Length:
                    payload = args[++index];
                    break;
                case "--version" when index + 1 < args.Length:
                    version = args[++index];
                    break;
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                default:
                    throw new InvalidOperationException("Usage: Vestigo.Playwright.Installer --payload <dir> --version <major.minor.build> --output <msi>");
            }
        }

        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Usage: Vestigo.Playwright.Installer --payload <dir> --version <major.minor.build> --output <msi>");
        string msi = Path.GetFullPath(output);
        return new BuildOptions(
            Path.GetFullPath(payload),
            ProductIdentity.ParseVersion(version),
            Path.GetDirectoryName(msi) ?? ".",
            Path.GetFileNameWithoutExtension(msi));
    }
}
