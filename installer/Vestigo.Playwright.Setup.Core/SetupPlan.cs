namespace Vestigo.Playwright.Setup;

public enum PrerequisiteMode
{
    External = 0,
    Managed = 1
}

public sealed record SetupInvocation(
    string? VestigoDirectory,
    PrerequisiteMode Mode,
    bool Quiet,
    bool VerifyOnly,
    bool Uninstall,
    bool Help)
{
    public const string HelpText =
        """
        Vestigo.Playwright.Setup.exe [--vestigo <absolute-path>] [--mode external|managed] [--quiet] [--verify-only] [--uninstall]

        Installs the plugin files. It does not download prerequisites, change Vestigo settings, or restart the Vestigo service.
        --mode external requires Vestigo. --mode managed also requires Node.js and Chrome or Edge.
        --verify-only checks the embedded installer hash and the selected prerequisites, then exits.
        """;
}

public static class SetupArguments
{
    public static SetupInvocation Parse(IReadOnlyList<string> args)
    {
        string? vestigo = null;
        PrerequisiteMode mode = PrerequisiteMode.External;
        bool quiet = false, verify = false, uninstall = false, help = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (arg is "--help" or "-h" or "-?" or "/?")
            {
                help = true;
                continue;
            }

            if (arg is "--quiet" or "-q")
            {
                Remember(seen, "--quiet");
                quiet = true;
                continue;
            }

            if (arg.Equals("--verify-only", StringComparison.OrdinalIgnoreCase))
            {
                Remember(seen, "--verify-only");
                verify = true;
                continue;
            }

            if (arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Remember(seen, "--uninstall");
                uninstall = true;
                continue;
            }

            if (TryTake(args, ref index, arg, "--mode", out string? modeText))
            {
                Remember(seen, "--mode");
                mode = modeText.ToLowerInvariant() switch
                {
                    "external" => PrerequisiteMode.External,
                    "managed" => PrerequisiteMode.Managed,
                    _ => throw new SetupException(ExitCodes.BadArguments, "Mode must be external or managed.")
                };
                continue;
            }

            if (TryTake(args, ref index, arg, "--vestigo", out string? path))
            {
                Remember(seen, "--vestigo");
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                    throw new SetupException(ExitCodes.BadArguments, "The Vestigo path must be absolute.");
                vestigo = path;
                continue;
            }

            throw new SetupException(ExitCodes.BadArguments, "The setup arguments were not recognized.");
        }

        if (help && args.Count != 1)
            throw new SetupException(ExitCodes.BadArguments, "Help does not take other arguments.");
        if (verify && uninstall)
            throw new SetupException(ExitCodes.BadArguments, "Verify-only and uninstall cannot be combined.");
        return new SetupInvocation(vestigo, mode, quiet, verify, uninstall, help);
    }

    private static void Remember(HashSet<string> seen, string name)
    {
        if (!seen.Add(name))
            throw new SetupException(ExitCodes.BadArguments, "A setup option was repeated.");
    }

    private static bool TryTake(IReadOnlyList<string> args, ref int index, string current, string name, out string value)
    {
        if (current.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Count)
                throw new SetupException(ExitCodes.BadArguments, "A setup option is missing its value.");
            value = args[++index];
            return true;
        }

        string prefix = name + "=";
        if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = current[prefix.Length..];
            return true;
        }

        value = "";
        return false;
    }
}

public static class SetupPlanner
{
    public static int Evaluate(SetupInvocation invocation, bool vestigoFound, bool nodeFound, bool browserFound)
    {
        if (invocation.Help || invocation.Uninstall)
            return ExitCodes.Success;
        return Prerequisites.Requirement(
            vestigoFound,
            invocation.Mode == PrerequisiteMode.External,
            nodeFound,
            browserFound);
    }
}

public static class MsiExit
{
    public static int Map(int nativeCode) => nativeCode switch
    {
        0 or 3010 or 1641 => ExitCodes.Success,
        1602 or 1223 => ExitCodes.Cancelled,
        _ => ExitCodes.InstallFailed
    };
}

public static class PayloadVerification
{
    public static void CopyVerified(Stream source, Stream destination, string expectedHex)
    {
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        buffer.Position = 0;
        HashCheck.RequireSha256(buffer, expectedHex);
        buffer.Position = 0;
        buffer.CopyTo(destination);
    }
}
