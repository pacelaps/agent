using PaceLaps.Agent.Core.Agent;
using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Core.Server;
using Microsoft.Extensions.Logging;

namespace PaceLaps.Agent.Hosting;

/// <summary>What the console's options change for one run; the tray app uses the settings file as it is.</summary>
public sealed record RuntimeOverrides
{
    public Uri? Server { get; init; }

    public int? League { get; init; }

    public MacroTable? Macros { get; init; }

    public string? AppIni { get; init; }

    public string? HarmlessCommand { get; init; }

    /// <summary>The replay channel (on by default; the console's <c>--no-replay</c> turns it off).</summary>
    public bool Replay { get; init; } = true;

    public CommandLimits? Limits { get; init; }

    /// <summary>The console's <c>--typed-way</c>: overrides the settings file's typedWithoutFocus.</summary>
    public bool? TypedWithoutFocus { get; init; }
}

/// <summary>
/// The agent as the operator runs it, shared by the tray app and the console (<c>run --server</c>). Sleeping is the
/// default and means silent (spec: States): no connection to the server exists then. Activate reads the settings file
/// and the credentials, opens the connection, builds the agent and runs the self-test; a failed self-test or Sleep
/// closes the connection again. Problems before the self-test (not paired, a bad settings file, the server out of
/// reach) leave the agent Sleeping and are shown as <see cref="Problem"/>.
/// </summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly RuntimeParts _parts;
    private readonly ConfigStore _config;
    private readonly CredentialStore _credentials;
    private readonly string _agentVersion;
    private readonly RuntimeOverrides _overrides;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>How long Sleep waits for the last heartbeat to get through before it closes the connection.</summary>
    public static readonly TimeSpan GoodbyeTimeout = TimeSpan.FromSeconds(3);

    private CancellationTokenSource? _stop;
    private Task _loop = Task.CompletedTask;

    public AgentRuntime(RuntimeParts parts, ConfigStore config, CredentialStore credentials, string agentVersion, RuntimeOverrides? overrides = null)
    {
        _parts = parts;
        _config = config;
        _credentials = credentials;
        _agentVersion = agentVersion;
        _overrides = overrides ?? new RuntimeOverrides();
        _logger = parts.Loggers.CreateLogger<AgentRuntime>();
    }

    /// <summary>The agent of this activation; null while Sleeping.</summary>
    public AgentHost? Host { get; private set; }

    public IServerConnection? Connection { get; private set; }

    /// <summary>The settings of this activation (or the last one).</summary>
    public AgentConfig? Settings { get; private set; }

    public AgentState State => Host?.States.State ?? AgentState.Sleeping;

    /// <summary>Why the last Activate did not get to the self-test; null once one did.</summary>
    public string? Problem { get; private set; }

    /// <summary>The last self-test, kept after the agent went back to sleep so the operator can read it.</summary>
    public SelfTestReport? LastSelfTest { get; private set; }

    /// <summary>When <see cref="LastSelfTest"/> ended.</summary>
    public DateTimeOffset? LastSelfTestAt { get; private set; }

    /// <summary>The commands finished lately, across Sleep and Activate (the app's Last commands).</summary>
    public RecentCommands RecentCommands { get; } = new();

    /// <summary>The steps of the self-test that runs now (or ran last), as they finish.</summary>
    public IReadOnlyList<SelfTestStep> SelfTestProgress => Volatile.Read(ref _selfTestProgress);

    private IReadOnlyList<SelfTestStep> _selfTestProgress = [];

    /// <summary>Why the agent last went back to Sleeping (a failed self-test names the step).</summary>
    public string? LastFailure { get; private set; }

    public bool Busy => _gate.CurrentCount == 0;

    public RuntimeParts Parts => _parts;

    /// <summary>From, to, reason: the agent's state changes, on the agent's thread.</summary>
    public event Action<AgentState, AgentState, string?>? StateChanged;

    /// <summary>Runs the self-test and, if it passes, keeps the agent Ready. Null when it did not get that far (see <see cref="Problem"/>).</summary>
    public async Task<SelfTestReport?> ActivateAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            if (Host is not null)
            {
                return null;
            }

            if (Prepare() is not { } prepared)
            {
                return null;
            }

            var (options, macros, credentials, appIni) = prepared;
            var connection = _parts.Connect(credentials);
            try
            {
                await connection.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                Refuse($"Cannot reach {credentials.Server}: {e.Message}");
                return null;
            }

            var host = new AgentHost(options, _parts.Sim, connection.Link, _parts.Typed, _parts.Macro, macros, _parts.Replay, _parts.Prompt,
                _parts.Clock, _parts.Loggers, _parts.MacroConfig(appIni), _parts.LocalFailsafeStore);
            host.States.Changed += (from, to, reason) => StateChanged?.Invoke(from, to, reason);
            host.Executor.Finished += RecentCommands.Add;
            Volatile.Write(ref _selfTestProgress, []);
            host.SelfTestStepFinished += step => Volatile.Write(ref _selfTestProgress, [.. _selfTestProgress, step]);
            (Host, Connection, Problem, LastFailure) = (host, connection, null, null);
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = _parts.RunLoop ? Task.Run(() => host.RunAsync(token), CancellationToken.None) : Task.CompletedTask;

            var report = await host.ActivateAsync(ct).ConfigureAwait(false);
            if (report is not null)
            {
                (LastSelfTest, LastSelfTestAt) = (report, _parts.Clock.UtcNow);
            }
            if (host.States.State == AgentState.Sleeping)
            {
                LastFailure = host.States.LastFailure;
                await CloseAsync().ConfigureAwait(false);
            }

            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back to Sleeping: the agent stops all traffic and the connection is closed.</summary>
    public async Task SleepAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Host is { } host)
            {
                await host.SayGoodbyeAsync(GoodbyeTimeout).ConfigureAwait(false);
                host.Sleep();
                LastFailure = null;
                await CloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>What the operator sees: the tray app's status window and the console's <c>status</c>.</summary>
    public AgentStatus Status()
    {
        var (settings, errors) = Settings is { } s && Host is not null ? (s, []) : _config.TryLoad();
        var credentials = _credentials.Load();
        return AgentStatus.Of(this, settings, errors, credentials, _overrides.Server ?? settings.Server ?? credentials?.Server);
    }

    public async ValueTask DisposeAsync()
    {
        await SleepAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private (AgentHostOptions Options, MacroTable Macros, AgentCredentials Credentials, string? AppIni)? Prepare()
    {
        var (config, errors) = _config.TryLoad();
        if (errors.Count > 0)
        {
            Refuse($"The settings file has errors: {string.Join("; ", errors)}");
            return null;
        }

        var credentials = _credentials.Load();
        var server = _overrides.Server ?? config.Server ?? credentials?.Server;
        if (server is null || credentials is null)
        {
            Refuse("Not paired yet.");
            return null;
        }

        if (!ServerAddress.Same(credentials.Server, server))
        {
            Refuse($"Paired with {credentials.Server}, not {server}. Pair again.");
            return null;
        }

        Settings = config;
        _parts.Typed.WithoutFocus = _overrides.TypedWithoutFocus ?? config.TypedWithoutFocus;
        var macros = _overrides.Macros ?? config.Macros.ToTable();
        var options = new AgentHostOptions
        {
            AgentId = credentials.AgentId,
            AgentVersion = _agentVersion,
            SelfTest = new SelfTestOptions
            {
                HarmlessCommand = _overrides.HarmlessCommand ?? new SelfTestOptions().HarmlessCommand,
                LeagueOverride = _overrides.League ?? config.League,
                MacroEnabled = macros.Count > 0,
                ReplayEnabled = _overrides.Replay,
            },
            Limits = _overrides.Limits ?? new CommandLimits(),
            LocalFailsafe = config.LocalFailsafe,
        };
        return (options, macros, credentials with { Server = server }, _overrides.AppIni ?? config.AppIni);
    }

    private void Refuse(string problem)
    {
        Problem = problem;
        _logger.LogWarning("Not activated: {Problem}", problem);
    }

    private async Task CloseAsync()
    {
        if (_stop is { } stop)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await _loop.ConfigureAwait(false);
            stop.Dispose();
            _stop = null;
        }

        if (Connection is { } connection)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        (Host, Connection) = (null, null);
    }
}
