using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace LinkHarvester.Resolution;

/// <summary>
/// Lazily installs Chromium browser binaries on first use.
/// Avoids requiring the user to run `playwright install` separately.
/// </summary>
public sealed class PlaywrightInstaller
{
    private readonly ILogger<PlaywrightInstaller> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _done;

    public PlaywrightInstaller(ILogger<PlaywrightInstaller> log) { _log = log; }

    public async Task EnsureInstalledAsync(CancellationToken ct)
    {
        if (_done) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_done) return;
            _log.LogInformation("Ensuring Playwright browsers are installed...");
            var exit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium", "--with-deps" });
            if (exit != 0)
                _log.LogWarning("playwright install exited with code {Code}", exit);
            _done = true;
        }
        finally { _gate.Release(); }
    }
}
