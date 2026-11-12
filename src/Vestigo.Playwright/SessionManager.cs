using System.Collections.Concurrent;

using ModelContextProtocol.Protocol;

using Vestigo.Extensibility;

namespace Vestigo.Playwright;

internal interface IUpstreamConnection : IAsyncDisposable
{
    Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken);
    Task<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken cancellationToken);
}

internal interface IUpstreamConnector
{
    Task<IUpstreamConnection> ConnectAsync(PluginSessionIdentity identity, CancellationToken cancellationToken);
    void Release(PluginSessionIdentity identity);
}

internal sealed class SessionManager : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<SessionKey, SessionSlot> _slots = [];
    private readonly Dictionary<SessionKey, LossRecord> _losses = [];
    private readonly Dictionary<SessionKey, PlaywrightException> _notes = [];
    private readonly SemaphoreSlim _startups;
    private readonly PlaywrightSettings _settings;
    private readonly IUpstreamConnector _connector;
    private readonly IClock _clock;
    private int _waiting;
    private int _disposed;
    private bool _shuttingDown;

    public SessionManager(PlaywrightSettings settings, IUpstreamConnector connector, IClock clock)
    {
        _settings = settings;
        _connector = connector;
        _clock = clock;
        _startups = new SemaphoreSlim(settings.MaxConcurrentStartups, settings.MaxConcurrentStartups);
    }

    public int Waiting
    {
        get { lock (_gate) return _waiting; }
    }

    public bool TryGetLoss(PluginSessionIdentity identity, out LossRecord? loss)
    {
        lock (_gate)
            return _losses.TryGetValue(KeyFor(identity), out loss);
    }

    public void Note(PluginSessionIdentity identity, PlaywrightException exception)
    {
        lock (_gate)
            _notes[KeyFor(identity)] = exception;
    }

    public bool TryGetNote(PluginSessionIdentity identity, out PlaywrightException? exception)
    {
        lock (_gate)
            return _notes.TryGetValue(KeyFor(identity), out exception);
    }

    public void ClearNote(PluginSessionIdentity identity)
    {
        lock (_gate)
            _notes.Remove(KeyFor(identity));
    }

    public async Task<SessionHold> AcquireAsync(PluginSessionIdentity identity, CancellationToken cancellationToken)
    {
        SessionKey key = KeyFor(identity);
        SessionSlot slot;
        Task<IUpstreamConnection> connecting;
        bool joinedConnect;
        lock (_gate)
        {
            if (_shuttingDown)
                throw new PlaywrightException(PlaywrightCodes.ShuttingDown, "Playwright is shutting down.");
            if (_losses.TryGetValue(key, out LossRecord? loss) && loss is not null)
                throw new PlaywrightException(loss.Code, loss.Message, sessionLost: true);
            if (!_slots.TryGetValue(key, out slot!))
            {
                if (_slots.Count >= _settings.MaxSessions)
                    throw new PlaywrightException(PlaywrightCodes.Capacity, "The Playwright session limit has been reached.");
                slot = new SessionSlot();
                _slots.Add(key, slot);
            }
            if (slot.Connection is not null)
            {
                connecting = Task.FromResult(slot.Connection);
                joinedConnect = false;
            }
            else
            {
                slot.Waiters++;
                joinedConnect = true;
                if (slot.Connecting is null)
                {
                    slot.ConnectCancellation = new CancellationTokenSource(_settings.StartupTimeout);
                    CancellationToken token = slot.ConnectCancellation.Token;
                    slot.Connecting = ConnectAsync(key, identity, slot, token);
                }
                connecting = slot.Connecting;
            }
        }

        IUpstreamConnection connection;
        try
        {
            connection = await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (joinedConnect)
                AbandonWaiter(key, slot);
            throw;
        }
        catch
        {
            if (joinedConnect)
                AbandonWaiter(key, slot);
            throw;
        }

        await EnterOperationAsync(slot, cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_shuttingDown)
                    throw new PlaywrightException(PlaywrightCodes.ShuttingDown, "Playwright is shutting down.");
                _losses.TryGetValue(key, out LossRecord? lost);
                if (lost is not null || !ReferenceEquals(slot.Connection, connection))
                    throw new PlaywrightException(lost?.Code ?? PlaywrightCodes.SessionLost, lost?.Message ?? "The Playwright session is no longer available.", sessionLost: true);
                slot.Operations++;
                slot.LastUsedUtc = _clock.UtcNow;
                if (joinedConnect)
                    slot.Waiters = Math.Max(0, slot.Waiters - 1);
            }
        }
        catch
        {
            slot.Operation.Release();
            throw;
        }
        return new SessionHold(this, key, slot, connection);
    }

    public async Task ExpireIdleAsync(CancellationToken cancellationToken)
    {
        List<SessionSlot> expired = [];
        lock (_gate)
        {
            if (_shuttingDown)
                return;
            DateTimeOffset now = _clock.UtcNow;
            foreach ((SessionKey key, SessionSlot slot) in _slots.ToArray())
            {
                if (slot.Connection is null || slot.Operations != 0 || now - slot.LastUsedUtc < _settings.IdleTimeout)
                    continue;
                if (!slot.Operation.Wait(0))
                    continue;
                try
                {
                    if (slot.Operations != 0 || slot.Connection is null)
                        continue;
                    expired.Add(slot);
                    _losses[key] = new LossRecord(PlaywrightCodes.SessionExpired, "The Playwright session expired. Choose a new application session ID.", slot.Catalog);
                    _slots.Remove(key);
                    slot.Connection = null;
                }
                finally
                {
                    slot.Operation.Release();
                }
            }
        }
        foreach (SessionSlot slot in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slot.Connected is not null)
                await slot.Connected.DisposeAsync().ConfigureAwait(false);
            if (slot.Identity is { } knownIdentity)
                _connector.Release(knownIdentity);
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _clock.UtcNow + _settings.ShutdownTimeout;
        lock (_gate)
            _shuttingDown = true;
        while (_clock.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            bool busy;
            lock (_gate)
                busy = _slots.Values.Any(slot => slot.Operations != 0);
            if (!busy)
                break;
            await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
        }
        List<SessionSlot> slots;
        lock (_gate)
        {
            slots = _slots.Values.ToList();
            _slots.Clear();
        }
        foreach (SessionSlot slot in slots)
        {
            slot.ConnectCancellation?.Cancel();
            if (slot.Connected is not null)
                await slot.Connected.DisposeAsync().ConfigureAwait(false);
            if (slot.Identity is { } knownIdentity)
                _connector.Release(knownIdentity);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IUpstreamConnection> ConnectAsync(SessionKey key, PluginSessionIdentity identity, SessionSlot slot, CancellationToken cancellationToken)
    {
        await _startups.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IUpstreamConnection connection = await _connector.ConnectAsync(identity, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_shuttingDown || _losses.ContainsKey(key))
                {
                    _ = connection.DisposeAsync();
                    throw new PlaywrightException(PlaywrightCodes.ShuttingDown, "Playwright is shutting down.");
                }
                slot.Connection = connection;
                slot.Connected = connection;
                slot.Identity = identity;
                slot.Connecting = null;
                slot.LastUsedUtc = _clock.UtcNow;
            }
            return connection;
        }
            catch
            {
                lock (_gate)
                {
                    slot.Connecting = null;
                    if (slot.Connection is null)
                        _slots.Remove(key);
                }
                throw;
            }
        finally
        {
            _startups.Release();
        }
    }

    private void AbandonWaiter(SessionKey key, SessionSlot slot)
    {
        lock (_gate)
        {
            slot.Waiters = Math.Max(0, slot.Waiters - 1);
            if (slot.Waiters == 0 && slot.Connection is null)
            {
                slot.ConnectCancellation?.Cancel();
                if (slot.Connecting is { } pending)
                    _ = pending.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                if (slot.Connecting is null)
                    _slots.Remove(key);
            }
        }
    }

    private async Task EnterOperationAsync(SessionSlot slot, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_waiting >= _settings.MaxWaitingOperations)
                throw new PlaywrightException(PlaywrightCodes.Busy, "Too many Playwright operations are waiting.");
            _waiting++;
        }
        try
        {
            await slot.Operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _waiting--;
        }
    }

    private async Task LoseAsync(SessionKey key, SessionSlot slot, string code, string message)
    {
        IUpstreamConnection? connection = null;
        PluginSessionIdentity? identity = null;
        lock (_gate)
        {
            IReadOnlyList<Tool>? catalog = slot.Catalog;
            _losses[key] = new LossRecord(code, message, catalog);
            _slots.Remove(key);
            connection = slot.Connected;
            identity = slot.Identity;
            slot.Connection = null;
            slot.Connected = null;
        }
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);
        if (identity is not null)
            _connector.Release(identity);
    }

    private SessionKey KeyFor(PluginSessionIdentity identity) =>
        _settings.ShareBrowser ? new SessionKey("*", "*") : new SessionKey(identity.OwnerId, identity.SessionId);

    internal sealed class SessionHold : IAsyncDisposable
    {
        private readonly SessionManager _manager;
        private readonly SessionKey _key;
        private readonly SessionSlot _slot;
        private readonly IUpstreamConnection _connection;
        private int _disposed;

        internal SessionHold(SessionManager manager, SessionKey key, SessionSlot slot, IUpstreamConnection connection)
        {
            _manager = manager;
            _key = key;
            _slot = slot;
            _connection = connection;
        }

        public async Task<IReadOnlyList<Tool>> ListAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_manager._settings.ListTimeout);
            try
            {
                IReadOnlyList<Tool> tools = await _connection.ListToolsAsync(timeout.Token).ConfigureAwait(false);
                _slot.Catalog = tools;
                return tools;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                await Fail(PlaywrightCodes.Timeout, "The Playwright catalog request timed out. Choose a new application session ID.").ConfigureAwait(false);
                throw new PlaywrightException(PlaywrightCodes.Timeout, "The Playwright catalog request timed out. Choose a new application session ID.", sessionLost: true);
            }
            catch (PlaywrightException)
            {
                throw;
            }
            catch (Exception)
            {
                await Fail(PlaywrightCodes.SessionLost, "The Playwright session ended. Choose a new application session ID. The failed action was not replayed.").ConfigureAwait(false);
                throw new PlaywrightException(PlaywrightCodes.SessionLost, "The Playwright session ended. Choose a new application session ID. The failed action was not replayed.", sessionLost: true);
            }
        }

        public async Task<CallToolResult> CallAsync(CallToolRequestParams request, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_manager._settings.CallTimeout);
            try
            {
                return await _connection.CallToolAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                await Fail(PlaywrightCodes.Timeout, "The Playwright operation timed out. Choose a new application session ID. The action was not replayed.").ConfigureAwait(false);
                throw new PlaywrightException(PlaywrightCodes.Timeout, "The Playwright operation timed out. Choose a new application session ID. The action was not replayed.", sessionLost: true);
            }
            catch (PlaywrightException)
            {
                throw;
            }
            catch (Exception)
            {
                await Fail(PlaywrightCodes.SessionLost, "The Playwright session ended. Choose a new application session ID. The failed action was not replayed.").ConfigureAwait(false);
                throw new PlaywrightException(PlaywrightCodes.SessionLost, "The Playwright session ended. Choose a new application session ID. The failed action was not replayed.", sessionLost: true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            lock (_manager._gate)
            {
                _slot.Operations = Math.Max(0, _slot.Operations - 1);
                _slot.LastUsedUtc = _manager._clock.UtcNow;
            }
            _slot.Operation.Release();
            await Task.CompletedTask;
        }

        private Task Fail(string code, string message) => _manager.LoseAsync(_key, _slot, code, message);
    }

    internal sealed class SessionSlot
    {
        public SemaphoreSlim Operation { get; } = new(1, 1);
        public IUpstreamConnection? Connection { get; set; }
        public IUpstreamConnection? Connected { get; set; }
        public Task<IUpstreamConnection>? Connecting { get; set; }
        public CancellationTokenSource? ConnectCancellation { get; set; }
        public PluginSessionIdentity? Identity { get; set; }
        public IReadOnlyList<Tool>? Catalog { get; set; }
        public int Waiters { get; set; }
        public int Operations { get; set; }
        public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    internal readonly record struct SessionKey(string OwnerId, string SessionId);
}

internal sealed record LossRecord(string Code, string Message, IReadOnlyList<Tool>? Catalog);
