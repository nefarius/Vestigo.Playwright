namespace Vestigo.Playwright;

internal static class PlaywrightCodes
{
    public const string Configuration = "playwright_configuration_invalid";
    public const string Unavailable = "playwright_backend_unavailable";
    public const string StartupTimeout = "playwright_startup_timeout";
    public const string SessionLost = "playwright_session_lost";
    public const string SessionExpired = "playwright_session_expired";
    public const string Timeout = "playwright_operation_timeout";
    public const string Busy = "playwright_busy";
    public const string Capacity = "playwright_capacity";
    public const string ShuttingDown = "playwright_shutting_down";
    public const string Unsupported = "playwright_unsupported";
    public const string StatusTool = "vestigo_backend_status";
}

/// <summary>Safe failure. Message text is shown to operators and models and must not contain secrets.</summary>
internal sealed class PlaywrightException : Exception
{
    public PlaywrightException(string code, string safeMessage, bool sessionLost = false)
        : base(safeMessage)
    {
        Code = code;
        SessionLost = sessionLost;
    }

    public string Code { get; }
    public bool SessionLost { get; }
}
