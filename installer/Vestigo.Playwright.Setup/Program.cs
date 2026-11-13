using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Vestigo.Playwright.Setup;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            SetupInvocation invocation = SetupArguments.Parse(args);
            if (invocation.Help)
            {
                Console.WriteLine(SetupInvocation.HelpText);
                return ExitCodes.Success;
            }

            if (!OperatingSystem.IsWindows())
                throw new SetupException(ExitCodes.InstallFailed, "Setup.exe installs with Windows Installer.");

            string vestigoDirectory = invocation.VestigoDirectory ?? HostLayout.DefaultDirectory(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            bool vestigoFound = HostLayout.ContainsHost(File.Exists, vestigoDirectory);
            bool nodeFound = NodeLayout.Found(File.Exists, NodeLayout.Candidates(
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
            int prerequisite = SetupPlanner.Evaluate(invocation, vestigoFound, nodeFound, WindowsBrowser.Installed());
            if (prerequisite != ExitCodes.Success)
                return prerequisite;

            string extracted = ExtractVerifiedPayload();
            try
            {
                if (invocation.VerifyOnly)
                    return ExitCodes.Success;
                return MsiExit.Map(RunInstaller(extracted, invocation));
            }
            finally
            {
                try
                {
                    File.Delete(extracted);
                }
                catch (IOException)
                {
                }
            }
        }
        catch (SetupException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.ExitCode;
        }
    }

    private static string ExtractVerifiedPayload()
    {
        if (SetupPayload.ExpectedHash.Length != 64)
            throw new SetupException(ExitCodes.BadArguments, "This setup build does not contain the plugin installer.");
        using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(SetupPayload.ResourceName);
        if (resource is null)
            throw new SetupException(ExitCodes.BadArguments, "This setup build does not contain the plugin installer.");
        string extracted = Path.Combine(Path.GetTempPath(), "vestigo-playwright-" + Guid.NewGuid().ToString("N") + ".msi");
        using var destination = new FileStream(extracted, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        PayloadVerification.CopyVerified(resource, destination, SetupPayload.ExpectedHash);
        return extracted;
    }

    private static int RunInstaller(string msi, SetupInvocation invocation)
    {
        var start = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false };
        if (invocation.Uninstall)
        {
            Guid productCode = ProductIdentity.ProductCode(SetupPayload.ProductVersion);
            start.ArgumentList.Add("/x");
            start.ArgumentList.Add(productCode.ToString("B"));
        }
        else
        {
            start.ArgumentList.Add("/i");
            start.ArgumentList.Add(msi);
        }

        start.ArgumentList.Add("/norestart");
        if (invocation.Quiet)
            start.ArgumentList.Add("/qn");
        using Process process = Process.Start(start) ?? throw new SetupException(ExitCodes.InstallFailed, "Windows Installer did not start.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static class WindowsBrowser
    {
        public static bool Installed() => OperatingSystem.IsWindows() && ReadInstalled();

        [SupportedOSPlatform("windows")]
        private static bool ReadInstalled() => BrowserLayout.Found(ReadDefaultValue, File.Exists);

        [SupportedOSPlatform("windows")]
        private static string? ReadDefaultValue(string hive, string key)
        {
            RegistryKey root = hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            using RegistryKey? opened = root.OpenSubKey(key);
            return opened?.GetValue(null) as string;
        }
    }
}
