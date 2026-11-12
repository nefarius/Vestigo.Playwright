using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Vestigo.Playwright;

internal sealed class OwnedProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TimeSpan _shutdown;
    private int _disposed;
    private bool _exited;

    private OwnedProcess(Process process, DiagnosticBuffer diagnostics, TimeSpan shutdown)
    {
        _process = process;
        Diagnostics = diagnostics;
        _shutdown = shutdown;
    }

    public int Id => _process.Id;

    public bool HasExited
    {
        get
        {
            if (_exited)
                return true;
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return _exited; }
        }
    }
    public Stream StandardInput => _process.StandardInput.BaseStream;
    public Stream StandardOutput => _process.StandardOutput.BaseStream;
    public DiagnosticBuffer Diagnostics { get; }

    public static OwnedProcess Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan shutdown, IEnumerable<string> secrets)
    {
        Directory.CreateDirectory(workingDirectory);
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new PlaywrightException(PlaywrightCodes.Unavailable, "The managed Playwright process did not start.");
        }
        catch (PlaywrightException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new PlaywrightException(PlaywrightCodes.Unavailable, "The managed Playwright process did not start.");
        }
        var diagnostics = new DiagnosticBuffer(secrets);
        process.ErrorDataReceived += (_, args) => diagnostics.Add(args.Data);
        process.BeginErrorReadLine();
        return new OwnedProcess(process, diagnostics, shutdown);
    }

    public async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited)
                throw new PlaywrightException(PlaywrightCodes.Unavailable, "The managed Playwright process exited before it accepted connections.");
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new PlaywrightException(PlaywrightCodes.StartupTimeout, "The managed Playwright process did not become ready.");
    }

    public static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(_shutdown).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The process already exited or could not be signalled. Ownership stays limited to this handle.
        }
        finally
        {
            try { _exited = _process.HasExited; }
            catch (InvalidOperationException) { _exited = true; }
            _process.Dispose();
        }
    }
}

internal static class ManagedCommand
{
    public static IReadOnlyList<string> Build(PlaywrightSettings settings, string outputDirectory, string? profileDirectory, int? port)
    {
        var arguments = new List<string>(settings.Arguments)
        {
            "--browser", settings.Browser
        };
        if (!settings.Headed)
            arguments.Add("--headless");
        arguments.Add("--ignore-roots");
        arguments.Add("--image-responses");
        arguments.Add("allow");
        arguments.Add("--output-dir");
        arguments.Add(outputDirectory);
        if (settings.Attach == AttachMode.Extension)
            arguments.Add("--extension");
        else if (settings.Attach == AttachMode.Cdp)
        {
            arguments.Add("--cdp-endpoint");
            arguments.Add(settings.CdpEndpoint!.AbsoluteUri);
        }
        else if (settings.ProfileMode == ProfileMode.Ephemeral)
            arguments.Add("--isolated");
        else if (!string.IsNullOrEmpty(profileDirectory))
        {
            arguments.Add("--user-data-dir");
            arguments.Add(profileDirectory);
        }
        if (settings.AllowedOrigins.Count > 0)
        {
            arguments.Add("--allowed-origins");
            arguments.Add(string.Join(';', settings.AllowedOrigins));
        }
        if (settings.Capabilities.Count > 0)
        {
            arguments.Add("--caps");
            arguments.Add(string.Join(',', settings.Capabilities));
        }
        if (port is int selected)
        {
            arguments.Add("--host");
            arguments.Add("127.0.0.1");
            arguments.Add("--port");
            arguments.Add(selected.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return arguments;
    }
}
