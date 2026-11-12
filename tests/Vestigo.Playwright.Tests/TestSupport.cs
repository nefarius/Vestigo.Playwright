using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Protocol;

using Vestigo.Extensibility;
using Vestigo.Playwright;

namespace Vestigo.Playwright.Tests;

internal sealed class ManualClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
}

internal sealed class FakeProbe : IDeploymentProbe
{
    public bool IsInteractiveDesktop { get; set; } = true;
    public bool BrowserInstalled { get; set; } = true;
    public bool FileExistsResult { get; set; } = true;
    public bool IsBrowserInstalled(string browser) => BrowserInstalled;
    public bool FileExists(string path) => FileExistsResult;
}

internal sealed class FakeConnector : IUpstreamConnector
{
    public int Connects;
    public Func<PluginSessionIdentity, CancellationToken, Task<IUpstreamConnection>>? Factory;
    public List<PluginSessionIdentity> Released { get; } = [];

    public Task<IUpstreamConnection> ConnectAsync(PluginSessionIdentity identity, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Connects);
        cancellationToken.ThrowIfCancellationRequested();
        return Factory?.Invoke(identity, cancellationToken) ?? Task.FromResult<IUpstreamConnection>(new FakeConnection());
    }

    public void Release(PluginSessionIdentity identity) => Released.Add(identity);
}

internal sealed class FakeConnection : IUpstreamConnection
{
    public bool Disposed;
    public bool ThrowOnDispose;
    public List<Tool> Tools { get; } = [ToolFactory.Named("browser_navigate")];
    public Func<CallToolRequestParams, CancellationToken, Task<CallToolResult>>? Call;

    public Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Tool>>(Tools);
    }

    public Task<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Call?.Invoke(request, cancellationToken) ?? Task.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = "ok" }]
        });
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        if (ThrowOnDispose)
            throw new IOException("dispose failed");
        return ValueTask.CompletedTask;
    }
}

internal static class ToolFactory
{
    public static Tool Named(string name) => new()
    {
        Name = name,
        Description = name,
        InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""")
    };
}

internal static class TestSettings
{
    public static PlaywrightSettings Create(int sessions = 4, int waiting = 8, TimeSpan? idle = null, bool share = false) => new()
    {
        Mode = BackendMode.Managed,
        Browser = "msedge",
        Prefix = "playwright_",
        ExecutablePath = OperatingSystem.IsWindows() ? @"C:\backend\node.exe" : "/usr/bin/node",
        ProfilesRoot = OperatingSystem.IsWindows() ? @"C:\state\profiles" : "/tmp/profiles",
        ArtifactsRoot = OperatingSystem.IsWindows() ? @"C:\state\artifacts" : "/tmp/artifacts",
        ContentDirectory = OperatingSystem.IsWindows() ? @"C:\plugin\content" : "/tmp/content",
        MaxSessions = sessions,
        MaxWaitingOperations = waiting,
        MaxConcurrentStartups = 2,
        IdleTimeout = idle ?? TimeSpan.FromMinutes(30),
        ShareBrowser = share,
        AllowSharedBrowser = share
    };

    public static IConfiguration Config(params (string Key, string Value)[] pairs)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in pairs)
            data[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    public static PluginSessionIdentity Identity(string owner, string session) => new(owner, session);

    public static PlaywrightToolProvider Provider(SessionManager sessions, PlaywrightSettings? settings = null) =>
        new(settings ?? Create(), sessions, NullLogger<PlaywrightToolProvider>.Instance);
}
