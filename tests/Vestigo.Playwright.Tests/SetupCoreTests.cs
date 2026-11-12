using System.Text.Json.Nodes;

using Vestigo.Playwright.Setup;

namespace Vestigo.Playwright.Tests;

public sealed class SetupCoreTests
{
    [Fact]
    public void Hash_AcceptsThePublishedDigestAndRejectsAMismatch()
    {
        byte[] content = "playwright-backend"u8.ToArray();
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        HashCheck.RequireSha256(new MemoryStream(content), digest.ToLowerInvariant());
        SetupException mismatch = Assert.Throws<SetupException>(() => HashCheck.RequireSha256(new MemoryStream(content), new string('a', 64)));
        Assert.Equal(ExitCodes.IntegrityMismatch, mismatch.ExitCode);
        Assert.DoesNotContain("playwright-backend", mismatch.ToString());
    }

    [Fact]
    public void Merge_EnablesTheModuleWithoutReplacingTrustOrOtherSettings()
    {
        const string original = """
            {
              "Vestigo": {
                "ApiTokens": [ "host-token-value" ],
                "Plugins": { "Enabled": true, "TrustMode": "PinnedSha256", "PackageHashes": { "playwright": "abc" } },
                "Modules": {
                  "Jobs": { "Enabled": true },
                  "Playwright": { "Browser": "chrome", "Enabled": false }
                }
              }
            }
            """;
        MergeResult merged = SettingsMerge.Merge(original, Activation.EnableLoadingAndModule);
        JsonObject root = Assert.IsType<JsonObject>(JsonNode.Parse(merged.Json));
        JsonObject vestigo = Assert.IsType<JsonObject>(root["Vestigo"]);
        Assert.Equal("host-token-value", vestigo["ApiTokens"]![0]!.GetValue<string>());
        Assert.Equal("PinnedSha256", vestigo["Plugins"]!["TrustMode"]!.GetValue<string>());
        Assert.True(vestigo["Plugins"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal("abc", vestigo["Plugins"]!["PackageHashes"]!["playwright"]!.GetValue<string>());
        Assert.True(vestigo["Modules"]!["Jobs"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal("chrome", vestigo["Modules"]!["Playwright"]!["Browser"]!.GetValue<string>());
        Assert.True(vestigo["Modules"]!["Playwright"]!["Enabled"]!.GetValue<bool>());

        MergeResult blocked = SettingsMerge.Merge(original.Replace("PinnedSha256", "Disabled", StringComparison.Ordinal), Activation.EnableLoadingAndModule);
        Assert.False(blocked.Changed);
        Assert.Equal("Disabled", JsonNode.Parse(blocked.Json)!["Vestigo"]!["Plugins"]!["TrustMode"]!.GetValue<string>());
        Assert.False(JsonNode.Parse(blocked.Json)!["Vestigo"]!["Modules"]!["Playwright"]!["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Merge_ReplacesExistingKeysWithoutChangingTheirCase()
    {
        const string original = """
            {
              "vestigo": {
                "plugins": { "enabled": false, "trustMode": "AdministratorManaged" },
                "modules": { "playwright": { "browser": "msedge", "enabled": false } }
              }
            }
            """;
        MergeResult merged = SettingsMerge.Merge(original, Activation.EnableLoadingAndModule);
        JsonObject vestigo = Assert.IsType<JsonObject>(JsonNode.Parse(merged.Json)!["vestigo"]);
        Assert.Null(vestigo["Plugins"]);
        Assert.Null(vestigo["Modules"]);
        Assert.True(vestigo["plugins"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("AdministratorManaged", vestigo["plugins"]!["trustMode"]!.GetValue<string>());
        Assert.Equal("msedge", vestigo["modules"]!["playwright"]!["browser"]!.GetValue<string>());
        Assert.True(vestigo["modules"]!["playwright"]!["enabled"]!.GetValue<bool>());
        Assert.Null(vestigo["modules"]!["Playwright"]);
    }

    [Fact]
    public void Prerequisites_AllowExternalModeWithoutALocalBrowser()
    {
        Assert.Equal(ExitCodes.Success, Prerequisites.Requirement(vestigoFound: true, externalOnly: true, nodeFound: false, browserFound: false));
        Assert.Equal(ExitCodes.MissingPrerequisite, Prerequisites.Requirement(vestigoFound: true, externalOnly: false, nodeFound: true, browserFound: false));
        Assert.Equal(ExitCodes.VestigoNotFound, Prerequisites.Requirement(vestigoFound: false, externalOnly: true, nodeFound: true, browserFound: true));
    }
}
