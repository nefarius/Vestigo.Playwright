using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

namespace Vestigo.Playwright;

internal static class ToolCatalog
{
    public const int MaxPages = 50;
    public const int MaxTools = 128;
    public const int MaxDescriptorBytes = 64 * 1024;
    public const int MaxCatalogBytes = 1024 * 1024;

    public static async Task<IReadOnlyList<Tool>> AggregateAsync(
        Func<string?, CancellationToken, Task<ListToolsResult>> listPage,
        CancellationToken cancellationToken)
    {
        var tools = new List<Tool>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        int catalogBytes = 0;
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListToolsResult result = await listPage(cursor, cancellationToken).ConfigureAwait(false);
            if (result.Tools is not null)
            {
                foreach (Tool tool in result.Tools)
                {
                    if (string.IsNullOrEmpty(tool.Name) || !names.Add(tool.Name))
                        continue;
                    if (tools.Count >= MaxTools)
                        throw new PlaywrightException(PlaywrightCodes.Unavailable, "The Playwright catalog exceeded the tool limit.");
                    int size = DescriptorBytes(tool);
                    if (size > MaxDescriptorBytes || catalogBytes > MaxCatalogBytes - size)
                        throw new PlaywrightException(PlaywrightCodes.Unavailable, "The Playwright catalog exceeded the descriptor size limit.");
                    catalogBytes += size;
                    tools.Add(tool);
                }
            }
            if (string.IsNullOrEmpty(result.NextCursor) || string.Equals(result.NextCursor, cursor, StringComparison.Ordinal))
                return tools;
            cursor = result.NextCursor;
        }
        throw new PlaywrightException(PlaywrightCodes.Unavailable, "The Playwright catalog exceeded the page limit.");
    }

    private static int DescriptorBytes(Tool tool)
    {
        try { return JsonSerializer.SerializeToUtf8Bytes(tool).Length; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new PlaywrightException(PlaywrightCodes.Unavailable, "The Playwright catalog contained a tool descriptor that could not be measured.");
        }
    }
}

internal static class RequestForwarder
{
    public static CallToolRequestParams Forward(CallToolRequestParams request)
    {
        JsonObject? meta = null;
        if (request.Meta is { Count: > 0 })
        {
            meta = (JsonObject)request.Meta.DeepClone();
            meta.Remove("progressToken");
            meta.Remove(Vestigo.Extensibility.PluginSessionIdentity.MetadataKey);
            if (meta.Count == 0)
                meta = null;
        }
        return new CallToolRequestParams
        {
            Name = request.Name,
            Arguments = request.Arguments,
            Meta = meta
        };
    }
}

internal static class ResultSanitizer
{
    public static CallToolResult Sanitize(CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0)
            return result;
        var content = new List<ContentBlock>(result.Content.Count + 1);
        bool mentionedPath = false;
        foreach (ContentBlock block in result.Content)
        {
            if (block is ResourceLinkBlock)
            {
                content.Add(new TextContentBlock
                {
                    Text = "Vestigo does not proxy MCP resources. The backend referenced a local artifact that clients cannot read through this plugin."
                });
                continue;
            }
            content.Add(block);
            if (block is TextContentBlock text && IsBarePath(text.Text))
                mentionedPath = true;
        }
        if (mentionedPath)
        {
            content.Add(new TextContentBlock
            {
                Text = "That path is local to the Playwright backend. Vestigo does not expose it as an MCP resource."
            });
        }
        result.Content = content;
        return result;
    }

    private static bool IsBarePath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\n'))
            return false;
        string trimmed = text.Trim();
        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return true;
        return Path.IsPathRooted(trimmed);
    }
}

internal static class ToolResults
{
    public static CallToolResult Error(PlaywrightException exception) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = exception.Code + ": " + exception.Message }]
    };

    public static Tool StatusTool() => new()
    {
        Name = PlaywrightCodes.StatusTool,
        Title = "Playwright backend status",
        Description = "Explains why official Playwright tools are unavailable for this application session.",
        InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""")
    };
}
