using Vestigo.Playwright;

namespace Vestigo.Playwright.Tests;

public sealed class ProcessTests
{
    [Fact]
    public async Task OwnedProcess_TerminatesOnlyItsOwnProcess()
    {
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        OwnedProcess first = OwnedProcess.Start(ping, ["-n", "30", "127.0.0.1"], Path.GetTempPath(), TimeSpan.FromSeconds(5), []);
        OwnedProcess second = OwnedProcess.Start(ping, ["-n", "30", "127.0.0.1"], Path.GetTempPath(), TimeSpan.FromSeconds(5), []);
        try
        {
            Assert.NotEqual(first.Id, second.Id);
            await first.DisposeAsync();
            Assert.True(first.HasExited);
            Assert.False(second.HasExited);
        }
        finally
        {
            await second.DisposeAsync();
        }
        Assert.True(second.HasExited);
    }
}
