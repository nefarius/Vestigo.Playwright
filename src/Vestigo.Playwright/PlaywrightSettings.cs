using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;

namespace Vestigo.Playwright;

internal enum BackendMode { Managed, External }

internal enum ManagedTransportKind { Stdio, Http }

internal enum ExternalTransportKind { StreamableHttp, Sse, Auto }

internal enum ExternalIsolation { PerSession, Exclusive }

internal enum ProfileMode { Ephemeral, Persistent }

internal enum AttachMode { None, Cdp, Extension }

internal sealed record PlaywrightSettings
{
    public const string DefaultPrefix = "playwright_";
    private static readonly Regex PrefixPattern = new(@"\A[a-z][a-z0-9_]{0,30}_\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CapabilityPattern = new(@"\A[a-z][a-z0-9]*\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] DeniedArguments =
    [
        "--no-sandbox", "--shared-browser-context", "--extension", "--cdp-endpoint", "--allow-unrestricted-file-access",
        "--host", "--port", "--user-data-dir", "--isolated", "--browser", "--executable-path"
    ];

    public required BackendMode Mode { get; init; }
    public required string Browser { get; init; }
    public bool Headed { get; init; } = true;
    public required string Prefix { get; init; }
    public string? ExecutablePath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public ManagedTransportKind ManagedTransport { get; init; } = ManagedTransportKind.Stdio;
    public Uri? ExternalEndpoint { get; init; }
    public ExternalTransportKind ExternalTransport { get; init; } = ExternalTransportKind.StreamableHttp;
    public ExternalIsolation ExternalIsolation { get; init; } = ExternalIsolation.PerSession;
    public IReadOnlyDictionary<string, string> ExternalHeaders { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool AllowUnauthenticatedLoopback { get; init; }
    public bool AllowInsecureRemote { get; init; }
    public bool AllowSharedBrowser { get; init; }
    public bool ShareBrowser { get; init; }
    public ProfileMode ProfileMode { get; init; } = ProfileMode.Ephemeral;
    public AttachMode Attach { get; init; } = AttachMode.None;
    public Uri? CdpEndpoint { get; init; }
    public required string ProfilesRoot { get; init; }
    public required string ArtifactsRoot { get; init; }
    public required string ContentDirectory { get; init; }
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public int MaxSessions { get; init; } = 8;
    public int MaxConcurrentStartups { get; init; } = 2;
    public int MaxWaitingOperations { get; init; } = 32;
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ListTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public IReadOnlyList<string> RedactionSecrets { get; init; } = [];

    public static string DefaultStateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nefarius Software Solutions",
        "Vestigo Playwright");

    public static PlaywrightSettings Parse(IConfiguration configuration, string contentDirectory)
    {
        BackendMode mode = ReadEnum(configuration, "Mode", BackendMode.Managed);
        string browser = (configuration["Browser"] ?? "msedge").Trim().ToLowerInvariant();
        if (browser is not ("chrome" or "msedge"))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "Browser must be chrome or msedge.");
        string prefix = configuration["Prefix"] ?? DefaultPrefix;
        if (!PrefixPattern.IsMatch(prefix))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "Prefix must be 2-32 characters: a lowercase letter, lowercase letters, digits or underscores, and a trailing underscore.");

        bool headed = ReadBool(configuration, "Headed", true);
        bool allowShared = ReadBool(configuration, "AllowSharedBrowser", false);
        bool share = ReadBool(configuration, "ShareBrowser", false);
        if (share && !allowShared)
            throw new PlaywrightException(PlaywrightCodes.Configuration, "ShareBrowser requires AllowSharedBrowser. Shared browsers are off unless an administrator opts in.");
        ProfileMode profile = ReadEnum(configuration, "ProfileMode", ProfileMode.Ephemeral);
        AttachMode attach = ReadEnum(configuration, "Attach", AttachMode.None);
        if (attach != AttachMode.None && !allowShared)
            throw new PlaywrightException(PlaywrightCodes.Configuration, "Attaching to an existing browser requires AllowSharedBrowser. Personal profiles are shared and are not isolated.");
        if (attach != AttachMode.None && mode != BackendMode.Managed)
            throw new PlaywrightException(PlaywrightCodes.Configuration, "Browser attachment is a managed-backend option. For an external endpoint, configure attachment on that endpoint and choose Exclusive isolation or ShareBrowser.");

        var arguments = ReadList(configuration, "Arguments");
        foreach (string argument in arguments)
        {
            if (argument.Any(static character => char.IsControl(character)))
                throw new PlaywrightException(PlaywrightCodes.Configuration, "A managed argument contains a control character.");
            foreach (string denied in DeniedArguments)
            {
                if (argument.Equals(denied, StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith(denied + "=", StringComparison.OrdinalIgnoreCase))
                    throw new PlaywrightException(PlaywrightCodes.Configuration, "A managed argument uses a flag reserved by the plugin.");
            }
        }

        string? executable = configuration["ExecutablePath"];
        Uri? external = null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ExternalIsolation isolation = ReadEnum(configuration, "ExternalIsolation", ExternalIsolation.PerSession);
        if (share && isolation == ExternalIsolation.Exclusive)
            throw new PlaywrightException(PlaywrightCodes.Configuration, "ShareBrowser and Exclusive external isolation cannot be combined.");
        if (mode == BackendMode.External)
        {
            external = ParseEndpoint(configuration["ExternalEndpoint"], allowRemoteHttp: ReadBool(configuration, "AllowInsecureRemote", false));
            headers = ReadHeaders(configuration.GetSection("ExternalHeaders"));
            bool loopback = external.IsLoopback;
            if (!loopback && headers.Count == 0)
                throw new PlaywrightException(PlaywrightCodes.Configuration, "A remote Playwright endpoint requires an administrator-configured request header. This check only tests that a header is present, not that it is a credential. The Playwright MCP backend must enforce its own authentication.");
            if (loopback && headers.Count == 0 && !ReadBool(configuration, "AllowUnauthenticatedLoopback", false))
                throw new PlaywrightException(PlaywrightCodes.Configuration, "Loopback binding does not authenticate local callers. Configure an upstream request header, which is not verified as a credential here, or set AllowUnauthenticatedLoopback for a protected local deployment. The Playwright MCP backend must enforce its own authentication.");
            if (isolation == ExternalIsolation.PerSession && share)
                throw new PlaywrightException(PlaywrightCodes.Configuration, "ShareBrowser cannot be combined with per-session external isolation.");
        }
        else if (string.IsNullOrWhiteSpace(executable))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "Managed mode requires an absolute executable path.");

        Uri? cdp = null;
        if (attach == AttachMode.Cdp)
            cdp = ParseCdp(configuration["CdpEndpoint"]);

        string profiles = AbsoluteRoot(configuration["ProfilesRoot"] ?? Path.Combine(DefaultStateRoot, "profiles"), contentDirectory, "ProfilesRoot");
        string artifacts = AbsoluteRoot(configuration["ArtifactsRoot"] ?? Path.Combine(DefaultStateRoot, "artifacts"), contentDirectory, "ArtifactsRoot");
        if (executable is not null)
            _ = AbsoluteFile(executable, contentDirectory);

        var origins = ReadList(configuration, "AllowedOrigins");
        foreach (string origin in origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("http" or "https"))
                throw new PlaywrightException(PlaywrightCodes.Configuration, "AllowedOrigins entries must be absolute http or https origins.");
        }
        var capabilities = ReadList(configuration, "Capabilities");
        if (capabilities.Any(item => !CapabilityPattern.IsMatch(item)))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "Capabilities must be lowercase tool-group names.");

        return new PlaywrightSettings
        {
            Mode = mode,
            Browser = browser,
            Headed = headed,
            Prefix = prefix,
            ExecutablePath = executable is null ? null : Path.GetFullPath(executable),
            Arguments = arguments,
            ManagedTransport = ReadEnum(configuration, "ManagedTransport", ManagedTransportKind.Stdio),
            ExternalEndpoint = external,
            ExternalTransport = ReadEnum(configuration, "ExternalTransport", ExternalTransportKind.StreamableHttp),
            ExternalIsolation = isolation,
            ExternalHeaders = headers,
            AllowUnauthenticatedLoopback = ReadBool(configuration, "AllowUnauthenticatedLoopback", false),
            AllowInsecureRemote = ReadBool(configuration, "AllowInsecureRemote", false),
            AllowSharedBrowser = allowShared,
            ShareBrowser = share,
            ProfileMode = profile,
            Attach = attach,
            CdpEndpoint = cdp,
            ProfilesRoot = profiles,
            ArtifactsRoot = artifacts,
            ContentDirectory = Path.GetFullPath(contentDirectory),
            AllowedOrigins = origins,
            Capabilities = capabilities,
            MaxSessions = ReadInt(configuration, "MaxSessions", 8, 1, 100),
            MaxConcurrentStartups = ReadInt(configuration, "MaxConcurrentStartups", 2, 1, 32),
            MaxWaitingOperations = ReadInt(configuration, "MaxWaitingOperations", 32, 1, 1000),
            IdleTimeout = ReadTime(configuration, "IdleTimeout", TimeSpan.FromMinutes(30)),
            StartupTimeout = ReadTime(configuration, "StartupTimeout", TimeSpan.FromSeconds(30)),
            ConnectionTimeout = ReadTime(configuration, "ConnectionTimeout", TimeSpan.FromSeconds(20)),
            CallTimeout = ReadTime(configuration, "CallTimeout", TimeSpan.FromMinutes(2)),
            ListTimeout = ReadTime(configuration, "ListTimeout", TimeSpan.FromSeconds(20)),
            ShutdownTimeout = ReadTime(configuration, "ShutdownTimeout", TimeSpan.FromSeconds(10)),
            RedactionSecrets = headers.Values.Where(static value => value.Length >= 4).ToArray()
        };
    }

    private static Uri ParseEndpoint(string? text, bool allowRemoteHttp)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "ExternalEndpoint must be an absolute http or https URL.");
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "ExternalEndpoint must be an http or https URL without user info, a query, or a fragment.");
        if (!uri.IsLoopback && uri.Scheme != "https" && !allowRemoteHttp)
            throw new PlaywrightException(PlaywrightCodes.Configuration, "A remote Playwright endpoint must use HTTPS unless AllowInsecureRemote is set.");
        return uri;
    }

    private static Uri ParseCdp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "CdpEndpoint must be an absolute loopback URL.");
        if (uri.Scheme is not ("http" or "https" or "ws" or "wss") || !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "CdpEndpoint must be a loopback http, https, ws, or wss URL without credentials.");
        return uri;
    }

    private static string AbsoluteRoot(string path, string contentDirectory, string name)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new PlaywrightException(PlaywrightCodes.Configuration, name + " must be an absolute path.");
        string full = Path.GetFullPath(path);
        if (IsInside(full, contentDirectory))
            throw new PlaywrightException(PlaywrightCodes.Configuration, name + " must be outside the plugin deployment directory.");
        if (Directory.Exists(full) && new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new PlaywrightException(PlaywrightCodes.Configuration, name + " must not be a reparse point.");
        return full;
    }

    private static string AbsoluteFile(string path, string contentDirectory)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "ExecutablePath must be an absolute path.");
        string full = Path.GetFullPath(path);
        if (IsInside(full, contentDirectory))
            throw new PlaywrightException(PlaywrightCodes.Configuration, "ExecutablePath must be outside the plugin deployment directory.");
        return full;
    }

    internal static bool IsInside(string path, string root)
    {
        string full = Path.GetFullPath(path);
        string rootFull = Path.GetFullPath(root);
        if (string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadHeaders(IConfiguration section)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (string.IsNullOrWhiteSpace(child.Key) || child.Key.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
                throw new PlaywrightException(PlaywrightCodes.Configuration, "An external header name is invalid.");
            if (string.IsNullOrEmpty(child.Value) || child.Value.Any(static character => character is '\r' or '\n'))
                throw new PlaywrightException(PlaywrightCodes.Configuration, "An external header value is invalid.");
            headers[child.Key] = child.Value;
        }
        return headers;
    }

    private static List<string> ReadList(IConfiguration configuration, string name) =>
        configuration.GetSection(name).GetChildren().Select(section => section.Value ?? "").Where(static value => value.Length > 0).ToList();

    private static bool ReadBool(IConfiguration configuration, string name, bool fallback)
    {
        string? text = configuration[name];
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        if (!bool.TryParse(text, out bool value))
            throw new PlaywrightException(PlaywrightCodes.Configuration, name + " must be true or false.");
        return value;
    }

    private static int ReadInt(IConfiguration configuration, string name, int fallback, int min, int max)
    {
        string? text = configuration[name];
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        if (!int.TryParse(text, out int value) || value < min || value > max)
            throw new PlaywrightException(PlaywrightCodes.Configuration, name + " is outside the supported range.");
        return value;
    }

    private static TimeSpan ReadTime(IConfiguration configuration, string name, TimeSpan fallback)
    {
        string? text = configuration[name];
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        if (!TimeSpan.TryParse(text, out TimeSpan value) || value <= TimeSpan.Zero || value > TimeSpan.FromHours(24))
            throw new PlaywrightException(PlaywrightCodes.Configuration, name + " must be a positive timespan of at most 24 hours.");
        return value;
    }

    private static T ReadEnum<T>(IConfiguration configuration, string name, T fallback) where T : struct, Enum
    {
        string? text = configuration[name];
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        if (!Enum.TryParse(text, ignoreCase: true, out T value) || !Enum.IsDefined(value))
            throw new PlaywrightException(PlaywrightCodes.Configuration, name + " is not a supported value.");
        return value;
    }
}
