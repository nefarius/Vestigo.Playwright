using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vestigo.Playwright.Setup;

public static class ExitCodes
{
    public const int Success = 0;
    public const int BadArguments = 1;
    public const int MissingPrerequisite = 2;
    public const int DownloadFailed = 3;
    public const int IntegrityMismatch = 4;
    public const int Cancelled = 5;
    public const int VestigoNotFound = 6;
    public const int InstallFailed = 7;
}

public sealed class SetupException : Exception
{
    public SetupException(int exitCode, string safeMessage) : base(safeMessage) => ExitCode = exitCode;
    public int ExitCode { get; }
}

public static class HashCheck
{
    public static void RequireSha256(Stream content, string expectedHex)
    {
        if (content is null || expectedHex.Length != 64 || expectedHex.Any(static character => !Uri.IsHexDigit(character)))
            throw new SetupException(ExitCodes.BadArguments, "The expected hash is not a SHA-256 digest.");
        string actual = Convert.ToHexString(SHA256.HashData(content));
        if (!actual.Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new SetupException(ExitCodes.IntegrityMismatch, "The file hash did not match the hash published with the signed release.");
    }
}

public enum Activation
{
    None = 0,
    EnableModuleOnly = 1,
    EnableLoadingAndModule = 2
}

public sealed record MergeResult(string Json, bool Changed, string? BlockedReason);

public static class SettingsMerge
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static MergeResult Merge(string appsettingsJson, Activation activation)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(appsettingsJson); }
        catch (JsonException) { throw new SetupException(ExitCodes.BadArguments, "Host settings are not valid JSON."); }
        if (parsed is not JsonObject document)
            throw new SetupException(ExitCodes.BadArguments, "Host settings must be a JSON object.");
        if (activation == Activation.None)
            return new(appsettingsJson, false, null);

        JsonObject vestigo = Object(document, "Vestigo");
        if (activation == Activation.EnableLoadingAndModule)
        {
            if (vestigo["Plugins"] is not JsonObject plugins)
                return new(appsettingsJson, false, "Plugin trust policy was left unchanged.");
            string? trust = Text(plugins["TrustMode"]);
            if (trust is not ("AdministratorManaged" or "PinnedSha256"))
                return new(appsettingsJson, false, "Plugin trust policy was left unchanged.");
            plugins["Enabled"] = true;
        }

        JsonObject modules = Object(vestigo, "Modules");
        JsonObject playwright = Object(modules, "Playwright");
        playwright["Enabled"] = true;
        return new(document.ToJsonString(Indented), true, null);
    }

    private static JsonObject Object(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
            return existing;
        if (parent[name] is not null)
            throw new SetupException(ExitCodes.BadArguments, "Host settings contain an unexpected JSON shape.");
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}

public static class Prerequisites
{
    public static int Requirement(bool vestigoFound, bool externalOnly, bool nodeFound, bool browserFound)
    {
        if (!vestigoFound)
            return ExitCodes.VestigoNotFound;
        if (!externalOnly && (!nodeFound || !browserFound))
            return ExitCodes.MissingPrerequisite;
        return ExitCodes.Success;
    }
}
