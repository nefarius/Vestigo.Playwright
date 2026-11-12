using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;

using Vestigo.Extensibility;

namespace Vestigo.Playwright;

internal sealed class PlaywrightToolProvider : IVestigoToolProvider
{
    private readonly PlaywrightSettings _settings;
    private readonly SessionManager _sessions;
    private readonly ILogger _logger;

    public PlaywrightToolProvider(PlaywrightSettings settings, SessionManager sessions, ILogger<PlaywrightToolProvider> logger)
    {
        _settings = settings;
        _sessions = sessions;
        _logger = logger;
    }

    public string Prefix => _settings.Prefix;
    public bool RequiresSessionIdentity => true;

    public async ValueTask<IReadOnlyList<Tool>> ListToolsAsync(PluginToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionIdentity is null)
            return [];
        try
        {
            await using SessionManager.SessionHold hold = await _sessions.AcquireAsync(context.SessionIdentity, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<Tool> tools = await hold.ListAsync(cancellationToken).ConfigureAwait(false);
            _sessions.ClearNote(context.SessionIdentity);
            return tools;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlaywrightException exception) when (exception.SessionLost &&
                                                     _sessions.TryGetLoss(context.SessionIdentity, out LossRecord? loss) &&
                                                     loss?.Catalog is { Count: > 0 } catalog)
        {
            _logger.LogInformation("Playwright session lost for a known catalog: {Code}", exception.Code);
            return catalog;
        }
        catch (PlaywrightException exception)
        {
            _sessions.Note(context.SessionIdentity, exception);
            _logger.LogInformation("Playwright catalog unavailable: {Code}", exception.Code);
            return [ToolResults.StatusTool()];
        }
    }

    public async ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams request, PluginToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionIdentity is null)
            return ToolResults.Error(new PlaywrightException(PlaywrightCodes.Configuration, "This Playwright tool requires an authenticated owner and an explicit application session ID."));
        if (string.Equals(request.Name, PlaywrightCodes.StatusTool, StringComparison.Ordinal))
            return Status(context.SessionIdentity);
        try
        {
            await using SessionManager.SessionHold hold = await _sessions.AcquireAsync(context.SessionIdentity, cancellationToken).ConfigureAwait(false);
            CallToolResult result = await hold.CallAsync(RequestForwarder.Forward(request), cancellationToken).ConfigureAwait(false);
            return ResultSanitizer.Sanitize(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlaywrightException exception)
        {
            _logger.LogInformation("Playwright call failed: {Code}", exception.Code);
            return ToolResults.Error(exception);
        }
    }

    private CallToolResult Status(PluginSessionIdentity identity)
    {
        if (_sessions.TryGetLoss(identity, out LossRecord? loss) && loss is not null)
            return ToolResults.Error(new PlaywrightException(loss.Code, loss.Message, sessionLost: true));
        if (_sessions.TryGetNote(identity, out PlaywrightException? note) && note is not null)
            return ToolResults.Error(note);
        return new CallToolResult
        {
            IsError = false,
            Content = [new TextContentBlock { Text = "No Playwright backend failure is recorded for this application session." }]
        };
    }
}
