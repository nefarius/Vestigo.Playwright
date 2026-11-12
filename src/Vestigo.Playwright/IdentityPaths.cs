using System.Security.Cryptography;
using System.Text;

using Vestigo.Extensibility;

namespace Vestigo.Playwright;

internal static class IdentityPaths
{
    public static string Leaf(PluginSessionIdentity identity)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.OwnerId + "\n" + identity.SessionId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal static class SecretRedactor
{
    public static string Redact(string? text, IEnumerable<string> secrets)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        foreach (string secret in secrets)
        {
            if (secret.Length < 4)
                continue;
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }
        return text;
    }
}

internal sealed class DiagnosticBuffer
{
    private readonly Queue<string> _lines = new();
    private readonly int _limit;
    private readonly IReadOnlyList<string> _secrets;

    public DiagnosticBuffer(IEnumerable<string> secrets, int limit = 20)
    {
        _secrets = secrets.Where(static secret => !string.IsNullOrWhiteSpace(secret)).ToArray();
        _limit = limit;
    }

    public void Add(string? line)
    {
        string redacted = SecretRedactor.Redact(line, _secrets).Replace('\r', ' ').Replace('\n', ' ');
        if (redacted.Length > 240)
            redacted = redacted[..240];
        lock (_lines)
        {
            _lines.Enqueue(redacted);
            while (_lines.Count > _limit)
                _lines.Dequeue();
        }
    }

    public string Snapshot()
    {
        lock (_lines)
            return string.Join(Environment.NewLine, _lines);
    }
}
