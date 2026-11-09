using Microsoft.Extensions.Logging;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Core.Updates;

namespace PaceLaps.Agent.Hosting;

/// <summary>
/// The update notice's check: reads the download server's public
/// <c>agent/latest.json</c> and <c>agent/changelog.json</c> at start and then once a day, in every state (the spec
/// allows update checks while sleeping). A plain GET: no agent id, no token, no league. Only release builds check
/// (a local build has no built-in server), and the setting <c>checkForUpdates</c> turns it off. A failed check is
/// logged and tried again an hour later; it never bothers the operator.
/// </summary>
public sealed class UpdateWatch : IDisposable
{
    public static readonly Uri DownloadServer = new("https://downloads.pacelaps.com/");

    /// <summary>Where the operator gets the release: the website's download page, with the checksum.</summary>
    public static readonly Uri DownloadPage = new("https://pacelaps.com/download");

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private readonly string _currentVersion;
    private readonly IClock _clock;
    private readonly Func<bool> _enabled;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private TimeSpan? _nextAt = TimeSpan.Zero;
    private int _running;

    /// <param name="enabled">Read before every check: a release build with the setting on.</param>
    public UpdateWatch(string currentVersion, IClock clock, Func<bool> enabled, ILogger logger, HttpMessageHandler? handler = null, Uri? server = null)
    {
        _currentVersion = currentVersion;
        _clock = clock;
        _enabled = enabled;
        _logger = logger;
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true)
        {
            BaseAddress = server ?? DownloadServer,
            Timeout = TimeSpan.FromSeconds(10),
        };
        // A generic client name: nothing that identifies the agent or the operator.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PaceLapsAgent");
    }

    /// <summary>The newer release, once a check found one; it stays until the program runs a newer version.</summary>
    public UpdateOffer? Offer { get; private set; }

    /// <summary>Starts a check when one is due (called twice a second by the tray); never more than one at a time.</summary>
    public Task CheckIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (_nextAt is not { } at || _clock.Elapsed < at || Interlocked.Exchange(ref _running, 1) == 1)
        {
            return Task.CompletedTask;
        }

        return RunAsync(cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_enabled())
            {
                _nextAt = _clock.Elapsed + Interval;
                return;
            }

            var latest = await _http.GetStringAsync("agent/latest.json", cancellationToken).ConfigureAwait(false);
            string? changelog = null;
            try
            {
                changelog = await _http.GetStringAsync("agent/changelog.json", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // The notice works without the release notes.
                _logger.LogInformation("Update check: no changelog ({Error})", e.Message);
            }

            var offer = UpdateCheck.Offer(_currentVersion, latest, changelog);
            if (offer is not null && offer.Version != Offer?.Version)
            {
                _logger.LogInformation("Update check: {Version} is available (running {Current})", offer.Version, _currentVersion);
            }

            Offer = offer ?? Offer;
            _nextAt = _clock.Elapsed + Interval;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Update check failed: {Error}", e.Message);
            _nextAt = _clock.Elapsed + RetryAfterFailure;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
