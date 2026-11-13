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

    [Fact]
    public void ProductCode_IsDeterministicAndSeparateFromVestigo()
    {
        Assert.Equal(Guid.Parse("6f3c8e21-4b7a-4d19-8c52-9e1a0d6b7f24"), ProductIdentity.UpgradeCode);
        Assert.NotEqual(Guid.Parse("7c2e1a44-6b8f-4d3a-9e15-2f6a8c0d4b71"), ProductIdentity.UpgradeCode);
        Assert.NotEqual(Guid.Parse("9a4d6e28-1c7b-4f05-8d3e-5b2c7a9f0e64"), ProductIdentity.UpgradeCode);
        Assert.Equal(Guid.Parse("6a3efdd9-227a-5d27-8282-6cf022f3a59d"), ProductIdentity.ProductCode("1.0.0"));
        Assert.Equal(Guid.Parse("bb464d5a-9211-5bb9-b1ed-20832bbbc134"), ProductIdentity.ProductCode("0.9.0"));
        Assert.Equal(ProductIdentity.ProductCode("1.0.0"), ProductIdentity.ProductCode("1.0.0.0"));
        Assert.NotEqual(ProductIdentity.ProductCode("1.0.0"), ProductIdentity.ProductCode("1.0.1"));
        Assert.Equal(Guid.Parse("2ed6657d-e927-568b-95e1-2665a8aea6a2"), Uuid5.Create(Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com"));
        Assert.Throws<SetupException>(() => ProductIdentity.ParseVersion("1.0.0.1"));
    }

    [Fact]
    public void Arguments_AcceptAbsoluteHostPathsAndRejectConflicts()
    {
        SetupInvocation parsed = SetupArguments.Parse(["--quiet", "--mode", "managed", "--vestigo", "/opt/vestigo"]);
        Assert.True(parsed.Quiet);
        Assert.Equal(PrerequisiteMode.Managed, parsed.Mode);
        Assert.Equal("/opt/vestigo", parsed.VestigoDirectory);

        SetupInvocation external = SetupArguments.Parse(["--mode=external", "--verify-only"]);
        Assert.Equal(PrerequisiteMode.External, external.Mode);
        Assert.True(external.VerifyOnly);

        Assert.Equal(ExitCodes.BadArguments, Assert.Throws<SetupException>(() => SetupArguments.Parse(["--vestigo", "relative\\vestigo"])).ExitCode);
        Assert.Equal(ExitCodes.BadArguments, Assert.Throws<SetupException>(() => SetupArguments.Parse(["--verify-only", "--uninstall"])).ExitCode);
        Assert.Equal(ExitCodes.BadArguments, Assert.Throws<SetupException>(() => SetupArguments.Parse(["--mode", "bundle"])).ExitCode);
        Assert.True(SetupArguments.Parse(["--help"]).Help);
    }

    [Fact]
    public void Probes_RequireTheHostAndOnlyRequireNodeAndBrowserForManagedMode()
    {
        Assert.True(HostLayout.ContainsHost(path => path.EndsWith("Vestigo.exe") || path.EndsWith("Vestigo.Abstractions.dll"), "/opt/vestigo"));
        Assert.False(HostLayout.ContainsHost(path => path.EndsWith("Vestigo.exe"), "/opt/vestigo"));
        Assert.Equal(Path.Combine("/opt", "Nefarius Software Solutions", "Vestigo"), HostLayout.DefaultDirectory("/opt"));

        Assert.True(NodeLayout.Found(path => path.EndsWith("node.exe"), NodeLayout.Candidates("C:\\Tools", "/opt", null, null)));
        Assert.False(NodeLayout.Found(_ => false, NodeLayout.Candidates(null, "/opt", null, null)));

        Assert.True(BrowserLayout.Found((hive, key) => hive == "HKCU" && key.EndsWith("msedge.exe") ? "/opt/msedge.exe" : null, path => path == "/opt/msedge.exe"));
        Assert.False(BrowserLayout.Found((_, _) => "/missing.exe", _ => false));

        var managed = new SetupInvocation(null, PrerequisiteMode.Managed, false, false, false, false);
        Assert.Equal(ExitCodes.MissingPrerequisite, SetupPlanner.Evaluate(managed, true, true, false));
        Assert.Equal(ExitCodes.VestigoNotFound, SetupPlanner.Evaluate(managed, false, true, true));
        var external = managed with { Mode = PrerequisiteMode.External };
        Assert.Equal(ExitCodes.Success, SetupPlanner.Evaluate(external, true, false, false));
        var uninstall = managed with { Uninstall = true };
        Assert.Equal(ExitCodes.Success, SetupPlanner.Evaluate(uninstall, false, false, false));
        Assert.NotEqual(ExitCodes.DownloadFailed, SetupPlanner.Evaluate(managed, true, false, false));
    }

    [Fact]
    public void EmbeddedPayload_IsCopiedOnlyWhenTheHashMatches()
    {
        byte[] content = "msi-bytes"u8.ToArray();
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        using var source = new MemoryStream(content);
        using var destination = new MemoryStream();
        PayloadVerification.CopyVerified(source, destination, digest);
        Assert.Equal(content, destination.ToArray());

        using var rejected = new MemoryStream();
        SetupException mismatch = Assert.Throws<SetupException>(() => PayloadVerification.CopyVerified(new MemoryStream(content), rejected, new string('b', 64)));
        Assert.Equal(ExitCodes.IntegrityMismatch, mismatch.ExitCode);
        Assert.Empty(rejected.ToArray());
        Assert.DoesNotContain("msi-bytes", mismatch.ToString());
    }

    [Fact]
    public void InstallerExit_MapsCancellationAndLeavesDownloadReserved()
    {
        Assert.Equal(ExitCodes.Success, MsiExit.Map(0));
        Assert.Equal(ExitCodes.Success, MsiExit.Map(3010));
        Assert.Equal(ExitCodes.Cancelled, MsiExit.Map(1602));
        Assert.Equal(ExitCodes.Cancelled, MsiExit.Map(1223));
        Assert.Equal(ExitCodes.InstallFailed, MsiExit.Map(1603));
        Assert.NotEqual(ExitCodes.DownloadFailed, MsiExit.Map(1603));
        Assert.Equal(3, ExitCodes.DownloadFailed);
    }
}
