using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Vestigo.Playwright.FakeBackend;

public static class Program
{
    public static async Task Main(string[] args)
    {
        _ = args;
        var state = new PageState();
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "fake-playwright", Version = "1.0.0" },
            ToolCollection =
            [
                McpServerTool.Create((string url) => state.Navigate(url), new McpServerToolCreateOptions { Name = "browser_navigate", Description = "Open a URL in this process." }),
                McpServerTool.Create(() => state.Url, new McpServerToolCreateOptions { Name = "browser_snapshot", Description = "Return the URL retained by this process." }),
                McpServerTool.Create(() => new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "click rejected" }],
                    StructuredContent = JsonSerializer.SerializeToElement(new { kept = true })
                }, new McpServerToolCreateOptions { Name = "browser_click", Description = "Return an intentional tool error." }),
                McpServerTool.Create(() => new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock { Text = "screenshot" },
                        ImageContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "image/png")
                    ],
                    StructuredContent = JsonSerializer.SerializeToElement(new { width = 1 })
                }, new McpServerToolCreateOptions { Name = "browser_take_screenshot", Description = "Return an inline image." })
            ]
        };
        await using McpServer server = McpServer.Create(new StdioServerTransport("fake-playwright", NullLoggerFactory.Instance), options);
        await server.RunAsync();
    }

    private sealed class PageState
    {
        public string Url { get; private set; } = "about:blank";
        public string Navigate(string url) => Url = url;
    }
}
