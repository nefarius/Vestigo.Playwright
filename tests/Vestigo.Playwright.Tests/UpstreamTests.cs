using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using Vestigo.Extensibility;
using Vestigo.Playwright;

namespace Vestigo.Playwright.Tests;

public sealed class PluginShapeTests
{
    [Fact]
    public async Task Module_IsOptInAndRegistersOneProvider()
    {
        VestigoModule module = Assert.Single(new Plugin().Modules);
        Assert.Equal("Playwright", module.Name);
        Assert.Empty(module.ToolTypes);
        Assert.False(module.EnabledByDefault);
        var services = new ServiceCollection();
        services.AddLogging();
        module.ConfigureServices(services, new PluginContext(TestSettings.Config(SettingsTestsAccess.Managed()), @"C:\plugin\content", AuthenticationEnabled: true));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IVestigoToolProvider tools = Assert.Single(provider.GetServices<IVestigoToolProvider>());
        Assert.Equal("playwright_", tools.Prefix);
        Assert.True(tools.RequiresSessionIdentity);
        Assert.IsType<PlaywrightMaintenanceService>(Assert.Single(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()));
    }
}

file static class SettingsTestsAccess
{
    public static (string Key, string Value)[] Managed() =>
    [
        ("Mode", "Managed"),
        ("Browser", "msedge"),
        ("ExecutablePath", @"C:\backend\node.exe"),
        ("ProfilesRoot", @"C:\state\profiles"),
        ("ArtifactsRoot", @"C:\state\artifacts")
    ];
}

public sealed class UpstreamRoundTripTests
{
    [Fact]
    public async Task ManagedStdio_PreservesResultsAndIsolatesProcesses()
    {
        string dll = typeof(FakeBackend.Program).Assembly.Location;
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        using var profiles = new TempDirectory();
        using var artifacts = new TempDirectory();
        var settings = TestSettings.Create() with
        {
            ExecutablePath = dotnet,
            Arguments = ["exec", dll],
            Headed = false,
            ProfilesRoot = profiles.Path,
            ArtifactsRoot = artifacts.Path,
            StartupTimeout = TimeSpan.FromSeconds(20),
            ConnectionTimeout = TimeSpan.FromSeconds(20),
            CallTimeout = TimeSpan.FromSeconds(20),
            ListTimeout = TimeSpan.FromSeconds(20)
        };
        var connector = new McpUpstreamConnector(settings, NullLoggerFactory.Instance);
        var sessions = new SessionManager(settings, connector, new ManualClock());
        PluginSessionIdentity first = TestSettings.Identity("owner-a", "session-0123456789abcdef");
        PluginSessionIdentity second = TestSettings.Identity("owner-b", "session-0123456789abcdef");
        try
        {
            await using (SessionManager.SessionHold hold = await sessions.AcquireAsync(first, CancellationToken.None))
            {
                IReadOnlyList<Tool> tools = await hold.ListAsync(CancellationToken.None);
                Assert.Contains(tools, tool => tool.Name == "browser_navigate");
                Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("playwright_", StringComparison.Ordinal));
                await hold.CallAsync(new CallToolRequestParams
                {
                    Name = "browser_navigate",
                    Arguments = new Dictionary<string, JsonElement> { ["url"] = JsonSerializer.SerializeToElement("https://one.example") }
                }, CancellationToken.None);
                CallToolResult snapshot = await hold.CallAsync(new CallToolRequestParams { Name = "browser_snapshot" }, CancellationToken.None);
                Assert.Contains("https://one.example", string.Join('\n', snapshot.Content!.OfType<TextContentBlock>().Select(block => block.Text)));
                CallToolResult click = await hold.CallAsync(new CallToolRequestParams { Name = "browser_click" }, CancellationToken.None);
                Assert.True(click.IsError);
                Assert.Contains("click rejected", string.Join('\n', click.Content!.OfType<TextContentBlock>().Select(block => block.Text)));
                Assert.True(click.StructuredContent!.Value.GetProperty("kept").GetBoolean());
                CallToolResult image = await hold.CallAsync(new CallToolRequestParams { Name = "browser_take_screenshot" }, CancellationToken.None);
                Assert.Contains(image.Content!, block => block is ImageContentBlock);
            }
            await using SessionManager.SessionHold other = await sessions.AcquireAsync(second, CancellationToken.None);
            CallToolResult isolated = await other.CallAsync(new CallToolRequestParams { Name = "browser_snapshot" }, CancellationToken.None);
            Assert.Contains("about:blank", string.Join('\n', isolated.Content!.OfType<TextContentBlock>().Select(block => block.Text)));
        }
        finally
        {
            await sessions.DisposeAsync();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vestigo-playwright-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
