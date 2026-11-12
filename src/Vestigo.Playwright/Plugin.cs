using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Vestigo.Extensibility;

namespace Vestigo.Playwright;

public sealed class Plugin : IVestigoPlugin
{
    public IReadOnlyList<VestigoModule> Modules { get; } = [new PlaywrightModule()];
}

public sealed class PlaywrightModule : VestigoModule
{
    public override string Name => "Playwright";
    public override string Description => "Forwards the official Playwright MCP browser tools for Chrome or Edge.";
    public override IReadOnlyList<Type> ToolTypes => [];
    public override IReadOnlyList<string> Capabilities => ["browser-automation", "page-content"];

    public override void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton(new ModuleSettingsSource(context.Settings, context.ContentDirectory));
        services.AddSingleton(static provider =>
        {
            ModuleSettingsSource source = provider.GetRequiredService<ModuleSettingsSource>();
            return PlaywrightSettings.Parse(source.Settings, source.ContentDirectory);
        });
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IDeploymentProbe, WindowsDeploymentProbe>();
        services.AddSingleton<IUpstreamConnector, McpUpstreamConnector>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<IVestigoToolProvider, PlaywrightToolProvider>();
        services.AddHostedService<PlaywrightMaintenanceService>();
    }
}

internal sealed class ModuleSettingsSource(Microsoft.Extensions.Configuration.IConfiguration settings, string contentDirectory)
{
    public Microsoft.Extensions.Configuration.IConfiguration Settings { get; } = settings;
    public string ContentDirectory { get; } = contentDirectory;
}

internal sealed class PlaywrightMaintenanceService : BackgroundService
{
    private readonly PlaywrightSettings _settings;
    private readonly SessionManager _sessions;
    private readonly IDeploymentProbe _probe;
    private readonly ILogger _logger;

    public PlaywrightMaintenanceService(PlaywrightSettings settings, SessionManager sessions, IDeploymentProbe probe, ILogger<PlaywrightMaintenanceService> logger)
    {
        _settings = settings;
        _sessions = sessions;
        _probe = probe;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RuntimeChecks.EnsureRunnable(_settings, _probe);
        _logger.LogInformation("Playwright module ready in {Mode} mode.", _settings.Mode);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await _sessions.ExpireIdleAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _sessions.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
