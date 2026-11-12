using System.Net;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using Vestigo.Extensibility;

namespace Vestigo.Playwright;

internal sealed class McpUpstreamConnector : IUpstreamConnector
{
    private readonly PlaywrightSettings _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly object _exclusive = new();
    private readonly object _directories = new();
    private PluginSessionIdentity? _exclusiveOwner;
    private bool _rootsReady;

    public McpUpstreamConnector(PlaywrightSettings settings, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpUpstreamConnector>();
    }

    public void Release(PluginSessionIdentity identity)
    {
        lock (_exclusive)
        {
            if (_exclusiveOwner == identity)
                _exclusiveOwner = null;
        }
    }

    public async Task<IUpstreamConnection> ConnectAsync(PluginSessionIdentity identity, CancellationToken cancellationToken)
    {
        bool reserved = Reserve(identity);
        try
        {
            EnsureRoots();
            string leaf = IdentityPaths.Leaf(identity);
            string artifacts = Path.Combine(_settings.ArtifactsRoot, leaf);
            Directory.CreateDirectory(artifacts);
            if (_settings.Mode == BackendMode.External)
                return await ConnectExternalAsync(artifacts, cancellationToken).ConfigureAwait(false);
            return await ConnectManagedAsync(artifacts, leaf, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (reserved)
                Release(identity);
            throw;
        }
    }

    private bool Reserve(PluginSessionIdentity identity)
    {
        if (_settings.Mode != BackendMode.External || _settings.ExternalIsolation != ExternalIsolation.Exclusive)
            return false;
        lock (_exclusive)
        {
            if (_exclusiveOwner is not null && _exclusiveOwner != identity)
                throw new PlaywrightException(PlaywrightCodes.Capacity, "This Playwright endpoint accepts one application session. Start it with --isolated for per-session contexts, or use another endpoint.");
            _exclusiveOwner = identity;
            return true;
        }
    }

    private void EnsureRoots()
    {
        lock (_directories)
        {
            if (_rootsReady)
                return;
            DirectoryProtection.CreateProtected(_settings.ProfilesRoot);
            DirectoryProtection.CreateProtected(_settings.ArtifactsRoot);
            _rootsReady = true;
        }
    }

    private async Task<IUpstreamConnection> ConnectExternalAsync(string artifacts, CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        var http = new HttpClient(handler, disposeHandler: true);
        try
        {
            var options = new HttpClientTransportOptions
            {
                Endpoint = _settings.ExternalEndpoint!,
                TransportMode = _settings.ExternalTransport switch
                {
                    ExternalTransportKind.Sse => HttpTransportMode.Sse,
                    ExternalTransportKind.Auto => HttpTransportMode.AutoDetect,
                    _ => HttpTransportMode.StreamableHttp
                },
                AdditionalHeaders = new Dictionary<string, string>(_settings.ExternalHeaders, StringComparer.OrdinalIgnoreCase),
                ConnectionTimeout = _settings.ConnectionTimeout,
                Name = "vestigo-playwright"
            };
            var transport = new HttpClientTransport(options, http, _loggerFactory, ownsHttpClient: false);
            McpClient client = await CreateClientAsync(transport, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Connected an external Playwright session.");
            return new ClientConnection(client, http, artifacts, persistentLock: null, deleteArtifacts: false);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    private async Task<IUpstreamConnection> ConnectManagedAsync(string artifacts, string leaf, CancellationToken cancellationToken)
    {
        string? profile = null;
        FileStream? profileLock = null;
        if (_settings.Attach == AttachMode.None && _settings.ProfileMode == ProfileMode.Persistent)
        {
            profile = Path.Combine(_settings.ProfilesRoot, leaf);
            Directory.CreateDirectory(profile);
            try
            {
                profileLock = new FileStream(Path.Combine(profile, ".vestigo.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                throw new PlaywrightException(PlaywrightCodes.Configuration, "The persistent profile is already locked. The plugin will not share it.");
            }
        }
        try
        {
            int? port = _settings.ManagedTransport == ManagedTransportKind.Http ? OwnedProcess.AllocateLoopbackPort() : null;
            IReadOnlyList<string> arguments = ManagedCommand.Build(_settings, artifacts, profile, port);
            OwnedProcess process = OwnedProcess.Start(_settings.ExecutablePath!, arguments, artifacts, _settings.ShutdownTimeout, _settings.RedactionSecrets);
            try
            {
                if (port is int httpPort)
                {
                    await process.WaitForPortAsync(httpPort, _settings.StartupTimeout, cancellationToken).ConfigureAwait(false);
                    var endpoint = new Uri($"http://127.0.0.1:{httpPort}/mcp");
                    var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
                    var http = new HttpClient(handler, disposeHandler: true);
                    try
                    {
                        var transport = new HttpClientTransport(new HttpClientTransportOptions
                        {
                            Endpoint = endpoint,
                            TransportMode = HttpTransportMode.StreamableHttp,
                            ConnectionTimeout = _settings.ConnectionTimeout,
                            Name = "vestigo-playwright-managed"
                        }, http, _loggerFactory, ownsHttpClient: false);
                        McpClient client = await CreateClientAsync(transport, cancellationToken).ConfigureAwait(false);
                        return new ClientConnection(client, process, http, artifacts, profileLock, deleteArtifacts: _settings.ProfileMode == ProfileMode.Ephemeral);
                    }
                    catch
                    {
                        http.Dispose();
                        throw;
                    }
                }
                var stdio = new StreamClientTransport(process.StandardInput, process.StandardOutput, _loggerFactory);
                McpClient stdioClient = await CreateClientAsync(stdio, cancellationToken).ConfigureAwait(false);
                return new ClientConnection(stdioClient, process, http: null, artifacts, profileLock, deleteArtifacts: _settings.ProfileMode == ProfileMode.Ephemeral);
            }
            catch
            {
                await process.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            profileLock?.Dispose();
            throw;
        }
    }

    private async Task<McpClient> CreateClientAsync(IClientTransport transport, CancellationToken cancellationToken)
    {
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "Vestigo.Playwright", Version = "1.0.0" },
            Capabilities = new ClientCapabilities(),
            InitializationTimeout = _settings.ConnectionTimeout
        };
        try
        {
            return await McpClient.CreateAsync(transport, options, _loggerFactory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Playwright backend connection failed: {Detail}", SecretRedactor.Redact(exception.Message, _settings.RedactionSecrets));
            throw new PlaywrightException(PlaywrightCodes.Unavailable, "The Playwright backend did not accept a connection.");
        }
    }

    private sealed class ClientConnection : IUpstreamConnection
    {
        private readonly McpClient _client;
        private readonly OwnedProcess? _process;
        private readonly HttpClient? _http;
        private readonly string _artifacts;
        private readonly FileStream? _lock;
        private readonly bool _deleteArtifacts;
        private int _disposed;

        public ClientConnection(McpClient client, HttpClient? http, string artifacts, FileStream? persistentLock, bool deleteArtifacts)
            : this(client, process: null, http, artifacts, persistentLock, deleteArtifacts)
        {
        }

        public ClientConnection(McpClient client, OwnedProcess? process, HttpClient? http, string artifacts, FileStream? persistentLock, bool deleteArtifacts)
        {
            _client = client;
            _process = process;
            _http = http;
            _artifacts = artifacts;
            _lock = persistentLock;
            _deleteArtifacts = deleteArtifacts;
        }

        public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken) =>
            await ToolCatalog.AggregateAsync(
                async (cursor, token) => await _client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

        public async Task<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken cancellationToken)
        {
            if (_process is { HasExited: true })
                throw new IOException("The owned Playwright process has exited.");
            return await _client.CallToolAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await _client.DisposeAsync().ConfigureAwait(false);
            if (_process is not null)
                await _process.DisposeAsync().ConfigureAwait(false);
            _http?.Dispose();
            _lock?.Dispose();
            if (_deleteArtifacts)
            {
                try { Directory.Delete(_artifacts, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
