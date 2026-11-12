using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

using Vestigo.Extensibility;
using Vestigo.Playwright;

namespace Vestigo.Playwright.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task Catalog_AggregatesPagesAndStopsOnRepeatedCursor()
    {
        int calls = 0;
        IReadOnlyList<Tool> tools = await ToolCatalog.AggregateAsync((_, _) =>
        {
            calls++;
            return Task.FromResult(calls switch
            {
                1 => new ListToolsResult { Tools = [ToolFactory.Named("browser_navigate"), ToolFactory.Named("browser_snapshot")], NextCursor = "page-2" },
                _ => new ListToolsResult { Tools = [ToolFactory.Named("browser_snapshot"), ToolFactory.Named("browser_click")], NextCursor = null }
            });
        }, CancellationToken.None);
        Assert.Equal(["browser_navigate", "browser_snapshot", "browser_click"], tools.Select(tool => tool.Name).ToArray());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Catalog_RejectsTooManyToolsAndOversizedDescriptors()
    {
        Tool[] many = Enumerable.Range(0, ToolCatalog.MaxTools + 1).Select(index => ToolFactory.Named("tool_" + index)).ToArray();
        PlaywrightException count = await Assert.ThrowsAsync<PlaywrightException>(() => ToolCatalog.AggregateAsync((_, _) => Task.FromResult(new ListToolsResult { Tools = many }), CancellationToken.None));
        Assert.Contains("tool limit", count.Message);
        var huge = ToolFactory.Named("browser_navigate");
        huge.Description = new string('x', ToolCatalog.MaxDescriptorBytes + 1);
        PlaywrightException size = await Assert.ThrowsAsync<PlaywrightException>(() => ToolCatalog.AggregateAsync((_, _) => Task.FromResult(new ListToolsResult { Tools = [huge] }), CancellationToken.None));
        Assert.Contains("descriptor size", size.Message);
    }

    [Fact]
    public void Forwarder_DropsProgressAndHostSessionMetadata()
    {
        var request = new CallToolRequestParams
        {
            Name = "browser_navigate",
            Arguments = new Dictionary<string, JsonElement> { ["url"] = JsonSerializer.SerializeToElement("https://example.test") },
            Meta = new JsonObject
            {
                ["progressToken"] = "token-1",
                [PluginSessionIdentity.MetadataKey] = "session-0123456789abcdef",
                ["keep"] = "visible"
            }
        };
        CallToolRequestParams forwarded = RequestForwarder.Forward(request);
        Assert.Equal("browser_navigate", forwarded.Name);
        Assert.Equal("https://example.test", forwarded.Arguments!["url"].GetString());
        Assert.False(forwarded.Meta!.ContainsKey("progressToken"));
        Assert.False(forwarded.Meta.ContainsKey(PluginSessionIdentity.MetadataKey));
        Assert.Equal("visible", forwarded.Meta["keep"]!.GetValue<string>());
    }

    [Fact]
    public void Sanitizer_RewritesResourceLinksAndLeavesImages()
    {
        var result = ResultSanitizer.Sanitize(new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new { kept = true }),
            Content =
            [
                new TextContentBlock { Text = "snapshot" },
                ImageContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "image/png"),
                new ResourceLinkBlock { Name = "shot", Uri = "file:///C:/secret/shot.png" },
                new TextContentBlock { Text = @"C:\state\artifacts\shot.png" }
            ]
        });
        Assert.False(result.IsError);
        Assert.True(result.StructuredContent!.Value.GetProperty("kept").GetBoolean());
        Assert.Contains(result.Content!, block => block is ImageContentBlock);
        Assert.DoesNotContain(result.Content!, block => block is ResourceLinkBlock);
        string text = string.Join('\n', result.Content!);
        Assert.Contains("does not proxy", text);
        Assert.DoesNotContain("secret", text);
        Assert.Contains(@"C:\state\artifacts\shot.png", text);
    }

    [Fact]
    public async Task Provider_PreservesUpstreamErrorsAndRefreshesCatalog()
    {
        var connector = new FakeConnector();
        var connection = new FakeConnection();
        connector.Factory = (_, _) => Task.FromResult<IUpstreamConnection>(connection);
        var sessions = new SessionManager(TestSettings.Create(), connector, new ManualClock());
        var provider = TestSettings.Provider(sessions);
        PluginSessionIdentity identity = TestSettings.Identity("owner-a", "session-0123456789abcdef");
        var context = new PluginToolContext("owner-a", identity);
        Assert.Equal("browser_navigate", Assert.Single(await provider.ListToolsAsync(context, CancellationToken.None)).Name);
        connection.Tools.Clear();
        connection.Tools.Add(ToolFactory.Named("browser_snapshot"));
        Assert.Equal("browser_snapshot", Assert.Single(await provider.ListToolsAsync(context, CancellationToken.None)).Name);

        connection.Call = (request, _) => Task.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "upstream said no" }],
            StructuredContent = JsonSerializer.SerializeToElement(new { code = "no" }),
            Meta = new JsonObject { ["keep"] = "result" }
        });
        CallToolResult result = await provider.CallToolAsync(new CallToolRequestParams
        {
            Name = "browser_snapshot",
            Arguments = new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement("hello") },
            Meta = new JsonObject { ["progressToken"] = "drop-me", ["keep"] = "arg" }
        }, context, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("upstream said no", string.Join('\n', result.Content!));
        Assert.Equal("no", result.StructuredContent!.Value.GetProperty("code").GetString());
        Assert.Equal("result", result.Meta!["keep"]!.GetValue<string>());
    }
}
