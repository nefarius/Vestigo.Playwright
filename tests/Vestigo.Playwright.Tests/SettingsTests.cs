using Microsoft.Extensions.Configuration;

using Vestigo.Playwright;

namespace Vestigo.Playwright.Tests;

public sealed class SettingsTests
{
    private const string Secret = "super-secret-value";

    [Fact]
    public void Parse_AcceptsManagedDefaultsAndExternalCredentials()
    {
        PlaywrightSettings managed = PlaywrightSettings.Parse(Config(ManagedPairs()), @"C:\plugin\content");
        Assert.Equal(BackendMode.Managed, managed.Mode);
        Assert.Equal("playwright_", managed.Prefix);
        Assert.True(managed.Headed);
        Assert.Equal("msedge", managed.Browser);
        Assert.Equal(ManagedTransportKind.Stdio, managed.ManagedTransport);

        var external = ManagedPairs().Where(pair => pair.Key is not ("Mode" or "ExecutablePath")).Append(("Mode", "External")).Append(("ExternalEndpoint", "http://127.0.0.1:8931/mcp")).Append(("ExternalHeaders:Authorization", "Bearer " + Secret));
        PlaywrightSettings parsed = PlaywrightSettings.Parse(Config(external.ToArray()), @"C:\plugin\content");
        Assert.Equal(BackendMode.External, parsed.Mode);
        Assert.Contains(parsed.RedactionSecrets, value => value.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsUnsafeValuesWithoutEchoingSecrets()
    {
        AssertSafe(() => PlaywrightSettings.Parse(Config(ManagedPairs(("ExecutablePath", @"C:\plugin\content\node.exe"))), @"C:\plugin\content"));
        AssertSafe(() => PlaywrightSettings.Parse(Config(ManagedPairs(("Arguments:0", "--no-sandbox"))), @"C:\plugin\content"));
        AssertSafe(() => PlaywrightSettings.Parse(Config(ManagedPairs(("ProfilesRoot", "relative"))), @"C:\plugin\content"));
        AssertSafe(() => PlaywrightSettings.Parse(Config(ManagedPairs(("Prefix", "Playwright"))), @"C:\plugin\content"));
        AssertSafe(() => PlaywrightSettings.Parse(Config(ManagedPairs(("Browser", "firefox"))), @"C:\plugin\content"));
        AssertSafe(() => PlaywrightSettings.Parse(Config(ManagedPairs(("ShareBrowser", "true"))), @"C:\plugin\content"));
        AssertSafe(() => PlaywrightSettings.Parse(Config(ManagedPairs(("Attach", "Cdp"), ("CdpEndpoint", "ws://127.0.0.1:9222"))), @"C:\plugin\content"));
        AssertSafe(() => PlaywrightSettings.Parse(Config([("Mode", "External"), ("Browser", "chrome"), ("ExternalEndpoint", "http://127.0.0.1/mcp?token=" + Secret), ("ProfilesRoot", @"C:\state\profiles"), ("ArtifactsRoot", @"C:\state\artifacts")]), @"C:\plugin\content"));
        AssertSafe(() => PlaywrightSettings.Parse(Config([("Mode", "External"), ("Browser", "chrome"), ("ExternalEndpoint", "http://example.test/mcp"), ("ProfilesRoot", @"C:\state\profiles"), ("ArtifactsRoot", @"C:\state\artifacts")]), @"C:\plugin\content"));
        AssertSafe(() => PlaywrightSettings.Parse(Config([("Mode", "External"), ("Browser", "chrome"), ("ExternalEndpoint", "http://user:pass@127.0.0.1/mcp"), ("ProfilesRoot", @"C:\state\profiles"), ("ArtifactsRoot", @"C:\state\artifacts")]), @"C:\plugin\content"));
    }

    [Fact]
    public void Runtime_RejectsHeadedServiceAndMissingExecutable()
    {
        PlaywrightSettings settings = PlaywrightSettings.Parse(Config(ManagedPairs()), @"C:\plugin\content");
        PlaywrightException service = Assert.Throws<PlaywrightException>(() => RuntimeChecks.EnsureRunnable(settings, new FakeProbe { IsInteractiveDesktop = false }));
        Assert.Equal(PlaywrightCodes.Configuration, service.Code);
        Assert.Contains("External", service.Message);
        PlaywrightException missing = Assert.Throws<PlaywrightException>(() => RuntimeChecks.EnsureRunnable(settings, new FakeProbe { FileExistsResult = false }));
        Assert.Contains("executable", missing.Message, StringComparison.OrdinalIgnoreCase);
        RuntimeChecks.EnsureRunnable(settings with { Headed = false }, new FakeProbe { IsInteractiveDesktop = false, FileExistsResult = true });
    }

    [Fact]
    public void Redactor_RemovesConfiguredSecrets()
    {
        var buffer = new DiagnosticBuffer([Secret]);
        buffer.Add("Authorization: Bearer " + Secret);
        Assert.DoesNotContain(Secret, buffer.Snapshot());
        Assert.Contains("***", buffer.Snapshot());
    }

    [Fact]
    public void Command_IsolatesProfilesAndBindsManagedHttpToLoopback()
    {
        PlaywrightSettings ephemeral = TestSettings.Create() with { Arguments = ["cli.js"], Headed = false, Capabilities = ["devtools"], AllowedOrigins = ["http://localhost:8080"] };
        IReadOnlyList<string> stdio = ManagedCommand.Build(ephemeral, @"C:\state\artifacts\one", profileDirectory: null, port: null);
        Assert.Contains("--isolated", stdio);
        Assert.Contains("--headless", stdio);
        Assert.Contains("--ignore-roots", stdio);
        Assert.DoesNotContain("--user-data-dir", stdio);
        Assert.DoesNotContain("--no-sandbox", stdio);
        Assert.Equal("127.0.0.1", ManagedCommand.Build(ephemeral with { ManagedTransport = ManagedTransportKind.Http }, @"C:\out", null, 8931)[Array.IndexOf(ManagedCommand.Build(ephemeral with { ManagedTransport = ManagedTransportKind.Http }, @"C:\out", null, 8931).ToArray(), "--host") + 1]);

        IReadOnlyList<string> persistent = ManagedCommand.Build(ephemeral with { ProfileMode = ProfileMode.Persistent, Headed = true }, @"C:\out", @"C:\state\profiles\abc", null);
        Assert.Contains("--user-data-dir", persistent);
        Assert.DoesNotContain("--isolated", persistent);
        Assert.DoesNotContain("--headless", persistent);

        IReadOnlyList<string> attached = ManagedCommand.Build(ephemeral with { Attach = AttachMode.Cdp, CdpEndpoint = new Uri("ws://127.0.0.1:9222"), AllowSharedBrowser = true }, @"C:\out", null, null);
        Assert.Contains("--cdp-endpoint", attached);
        Assert.DoesNotContain("--isolated", attached);
    }

    private static void AssertSafe(Action action)
    {
        PlaywrightException exception = Assert.Throws<PlaywrightException>(action);
        Assert.Equal(PlaywrightCodes.Configuration, exception.Code);
        Assert.DoesNotContain(Secret, exception.ToString());
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs) => TestSettings.Config(pairs);

    private static (string Key, string Value)[] ManagedPairs(params (string Key, string Value)[] extra) =>
    [
        ("Mode", "Managed"),
        ("Browser", "msedge"),
        ("ExecutablePath", OperatingSystem.IsWindows() ? @"C:\backend\node.exe" : "/usr/bin/node"),
        ("ProfilesRoot", OperatingSystem.IsWindows() ? @"C:\state\profiles" : "/tmp/profiles"),
        ("ArtifactsRoot", OperatingSystem.IsWindows() ? @"C:\state\artifacts" : "/tmp/artifacts"),
        .. extra
    ];
}
