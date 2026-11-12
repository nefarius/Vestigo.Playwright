using ModelContextProtocol.Protocol;

using Vestigo.Extensibility;
using Vestigo.Playwright;

namespace Vestigo.Playwright.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task Isolation_SeparatesOwnersAndSessionIds()
    {
        var connector = new FakeConnector();
        var sessions = new SessionManager(TestSettings.Create(), connector, new ManualClock());
        PluginSessionIdentity first = TestSettings.Identity("owner-a", "session-0123456789abcdef");
        PluginSessionIdentity sameOwner = TestSettings.Identity("owner-a", "session-fedcba9876543210");
        PluginSessionIdentity sameSession = TestSettings.Identity("owner-b", "session-0123456789abcdef");
        await using SessionManager.SessionHold one = await sessions.AcquireAsync(first, CancellationToken.None);
        await using SessionManager.SessionHold two = await sessions.AcquireAsync(sameOwner, CancellationToken.None);
        await using SessionManager.SessionHold three = await sessions.AcquireAsync(sameSession, CancellationToken.None);
        Assert.Equal(3, connector.Connects);
        Assert.Equal(3, new[] { IdentityPaths.Leaf(first), IdentityPaths.Leaf(sameOwner), IdentityPaths.Leaf(sameSession) }.Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentFirstUse_ConnectsOnce()
    {
        var connector = new FakeConnector();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.Factory = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new FakeConnection();
        };
        var sessions = new SessionManager(TestSettings.Create(), connector, new ManualClock());
        PluginSessionIdentity identity = TestSettings.Identity("owner-a", "session-0123456789abcdef");
        Task<SessionManager.SessionHold> first = sessions.AcquireAsync(identity, CancellationToken.None);
        Task<SessionManager.SessionHold> second = sessions.AcquireAsync(identity, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, connector.Connects);
        release.SetResult();
        SessionManager.SessionHold left = await first;
        await left.DisposeAsync();
        SessionManager.SessionHold right = await second;
        await right.DisposeAsync();
        Assert.Equal(1, connector.Connects);
    }

    [Fact]
    public async Task CapacityAndWaiting_AreBounded()
    {
        var connector = new FakeConnector();
        var sessions = new SessionManager(TestSettings.Create(sessions: 1, waiting: 1), connector, new ManualClock());
        PluginSessionIdentity identity = TestSettings.Identity("owner-a", "session-0123456789abcdef");
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeConnection
        {
            Call = async (_, token) =>
            {
                await blocked.Task.WaitAsync(token);
                return new CallToolResult { Content = [new TextContentBlock { Text = "done" }] };
            }
        };
        connector.Factory = (_, _) => Task.FromResult<IUpstreamConnection>(connection);
        var provider = TestSettings.Provider(sessions);
        Task<CallToolResult> active = provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, new PluginToolContext("owner-a", identity), CancellationToken.None).AsTask();
        await WaitUntilAsync(() => sessions.Waiting == 0 && connector.Connects == 1);
        Task<CallToolResult> waiting = provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, new PluginToolContext("owner-a", identity), CancellationToken.None).AsTask();
        await WaitUntilAsync(() => sessions.Waiting == 1);
        CallToolResult busy = await provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, new PluginToolContext("owner-a", identity), CancellationToken.None);
        Assert.Contains(PlaywrightCodes.Busy, Text(busy));
        PluginSessionIdentity other = TestSettings.Identity("owner-b", "session-0123456789abcdef");
        CallToolResult capacity = await provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, new PluginToolContext("owner-b", other), CancellationToken.None);
        Assert.Contains(PlaywrightCodes.Capacity, Text(capacity));
        blocked.SetResult();
        Assert.True((await active).IsError is not true);
        Assert.True((await waiting).IsError is not true);
    }

    [Fact]
    public async Task Cancellation_WhileWaitingConnectingAndCalling_DoesNotReplay()
    {
        var connector = new FakeConnector();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.Factory = async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return new FakeConnection();
        };
        var sessions = new SessionManager(TestSettings.Create(), connector, new ManualClock());
        PluginSessionIdentity identity = TestSettings.Identity("owner-a", "session-0123456789abcdef");
        using var connecting = new CancellationTokenSource();
        Task pending = sessions.AcquireAsync(identity, connecting.Token);
        connecting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        release.TrySetResult();
        connector.Factory = (_, _) => Task.FromResult<IUpstreamConnection>(new FakeConnection());
        await using (SessionManager.SessionHold recovered = await sessions.AcquireAsync(identity, CancellationToken.None))
            Assert.False(sessions.TryGetLoss(identity, out _));

        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saturatedConnector = new FakeConnector
        {
            Factory = async (candidate, token) =>
            {
                if (candidate.OwnerId == "owner-hold")
                {
                    blockerEntered.TrySetResult();
                    await blocker.Task;
                    return new FakeConnection();
                }
                token.ThrowIfCancellationRequested();
                return new FakeConnection();
            }
        };
        var saturated = new SessionManager(TestSettings.Create() with { MaxConcurrentStartups = 1, StartupTimeout = TimeSpan.FromMilliseconds(300) }, saturatedConnector, new ManualClock());
        PluginSessionIdentity holdIdentity = TestSettings.Identity("owner-hold", "session-hold-0123456789");
        Task<SessionManager.SessionHold> holding = saturated.AcquireAsync(holdIdentity, CancellationToken.None);
        await blockerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saturated.AcquireAsync(identity, CancellationToken.None));
        blocker.TrySetResult();
        await using (SessionManager.SessionHold held = await holding)
            await held.DisposeAsync();
        await using (SessionManager.SessionHold retried = await saturated.AcquireAsync(identity, CancellationToken.None))
            Assert.False(saturated.TryGetLoss(identity, out _));

        var connection = new FakeConnection();
        var callGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Call = async (_, token) =>
        {
            await callGate.Task.WaitAsync(token);
            return new CallToolResult { Content = [new TextContentBlock { Text = "done" }] };
        };
        connector.Factory = (_, _) => Task.FromResult<IUpstreamConnection>(connection);
        var provider = TestSettings.Provider(sessions);
        PluginSessionIdentity callingIdentity = TestSettings.Identity("owner-a", "session-calling-0123456789");
        using var calling = new CancellationTokenSource();
        ValueTask<CallToolResult> call = provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, new PluginToolContext("owner-a", callingIdentity), calling.Token);
        calling.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
        Assert.False(sessions.TryGetLoss(callingIdentity, out _));
        callGate.TrySetCanceled();
    }

    [Fact]
    public async Task CrashAndTimeout_TombstoneWithoutReplay()
    {
        var connector = new FakeConnector();
        var connection = new FakeConnection();
        var calls = 0;
        connection.Call = (_, _) =>
        {
            calls++;
            throw new IOException("backend secret exploded");
        };
        connector.Factory = (_, _) => Task.FromResult<IUpstreamConnection>(connection);
        var sessions = new SessionManager(TestSettings.Create() with { CallTimeout = TimeSpan.FromMilliseconds(100) }, connector, new ManualClock());
        var provider = TestSettings.Provider(sessions);
        PluginSessionIdentity identity = TestSettings.Identity("owner-a", "session-0123456789abcdef");
        var context = new PluginToolContext("owner-a", identity);
        CallToolResult failed = await provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, context, CancellationToken.None);
        Assert.True(failed.IsError);
        Assert.Contains(PlaywrightCodes.SessionLost, Text(failed));
        Assert.DoesNotContain("secret", Text(failed));
        CallToolResult again = await provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, context, CancellationToken.None);
        Assert.Contains(PlaywrightCodes.SessionLost, Text(again));
        Assert.Equal(1, connector.Connects);
        Assert.Equal(1, calls);
        Assert.True(connection.Disposed);

        var slow = new FakeConnection
        {
            Call = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new CallToolResult { Content = [new TextContentBlock { Text = "late" }] };
            }
        };
        var slowCalls = 0;
        connector.Factory = (_, _) =>
        {
            slowCalls++;
            return Task.FromResult<IUpstreamConnection>(slow);
        };
        PluginSessionIdentity timed = TestSettings.Identity("owner-a", "session-timeout-0123456789");
        var timedContext = new PluginToolContext("owner-a", timed);
        CallToolResult timedOut = await provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, timedContext, CancellationToken.None);
        Assert.Contains(PlaywrightCodes.Timeout, Text(timedOut));
        CallToolResult notReplayed = await provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, timedContext, CancellationToken.None);
        Assert.Contains(PlaywrightCodes.Timeout, Text(notReplayed));
        Assert.Equal(1, slowCalls);
        Assert.True(slow.Disposed);
    }

    [Fact]
    public async Task IdleExpiry_SkipsActiveOperationsThenRequiresANewSession()
    {
        var clock = new ManualClock();
        var connector = new FakeConnector();
        var connection = new FakeConnection();
        connector.Factory = (_, _) => Task.FromResult<IUpstreamConnection>(connection);
        var sessions = new SessionManager(TestSettings.Create(idle: TimeSpan.FromMinutes(5)), connector, clock);
        PluginSessionIdentity identity = TestSettings.Identity("owner-a", "session-0123456789abcdef");
        SessionManager.SessionHold hold = await sessions.AcquireAsync(identity, CancellationToken.None);
        await hold.ListAsync(CancellationToken.None);
        clock.UtcNow += TimeSpan.FromMinutes(10);
        await sessions.ExpireIdleAsync(CancellationToken.None);
        Assert.False(connection.Disposed);
        await hold.DisposeAsync();
        clock.UtcNow += TimeSpan.FromMinutes(10);
        await sessions.ExpireIdleAsync(CancellationToken.None);
        Assert.True(connection.Disposed);
        PlaywrightException expired = await Assert.ThrowsAsync<PlaywrightException>(() => sessions.AcquireAsync(identity, CancellationToken.None));
        Assert.Equal(PlaywrightCodes.SessionExpired, expired.Code);
        Assert.Equal(1, connector.Connects);
    }

    [Fact]
    public async Task SharedBrowser_LossDoesNotBlockAnotherIdentity()
    {
        var connector = new FakeConnector();
        var failing = new FakeConnection { Call = (_, _) => throw new IOException("down") };
        connector.Factory = (_, _) => Task.FromResult<IUpstreamConnection>(failing);
        PlaywrightSettings settings = TestSettings.Create(share: true);
        var sessions = new SessionManager(settings, connector, new ManualClock());
        var provider = TestSettings.Provider(sessions, settings);
        PluginSessionIdentity first = TestSettings.Identity("owner-a", "session-0123456789abcdef");
        PluginSessionIdentity second = TestSettings.Identity("owner-b", "session-0123456789abcdef");
        CallToolResult lost = await provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, new PluginToolContext("owner-a", first), CancellationToken.None);
        Assert.Contains(PlaywrightCodes.SessionLost, Text(lost));
        connector.Factory = (_, _) => Task.FromResult<IUpstreamConnection>(new FakeConnection());
        CallToolResult other = await provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, new PluginToolContext("owner-b", second), CancellationToken.None);
        Assert.True(other.IsError is not true);
        Assert.Equal(2, connector.Connects);
    }

    [Fact]
    public async Task MissingIdentity_FailsClosed()
    {
        var sessions = new SessionManager(TestSettings.Create(), new FakeConnector(), new ManualClock());
        var provider = TestSettings.Provider(sessions);
        Assert.Empty(await provider.ListToolsAsync(new PluginToolContext(null, null), CancellationToken.None));
        CallToolResult result = await provider.CallToolAsync(new CallToolRequestParams { Name = "browser_navigate" }, new PluginToolContext(null, null), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("session", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    private static string Text(CallToolResult result) => result.Content is null
        ? ""
        : string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("Condition was not reached.");
            await Task.Delay(20);
        }
    }
}
