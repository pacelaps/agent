using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.Time;

namespace PaceLaps.Agent.Hosting.Tray;

/// <summary>How a pairing ended, in words for the operator.</summary>
public sealed record PairingOutcome(bool Paired, string Message);

/// <summary>
/// The pairing screen's logic (spec: Identity and access &gt; Agents, device code): check the address, ask the
/// server for a code, show it, wait for the steward's approval, store the credentials (DPAPI) and make the paired
/// server the one in the settings file, so the next Activate uses it. A release build pairs only with the server
/// built into it (<see cref="ReleaseBuild"/>).
/// </summary>
public sealed class PairingFlow(CredentialStore credentials, ConfigStore config, string agentVersion, IClock clock,
    Func<Uri, PairingClient>? client = null, Uri? fixedServer = null)
{
    private readonly Func<Uri, PairingClient> _client = client ?? (url => new PairingClient(url, clock));

    /// <summary>The server of a release build; null lets the operator type one.</summary>
    public Uri? FixedServer { get; } = fixedServer;

    /// <param name="showCode">Called once the server gave a code: show it and where to enter it.</param>
    public async Task<PairingOutcome> PairAsync(string serverText, Action<PairingCodeMessage> showCode, CancellationToken ct)
    {
        if (!ServerAddress.TryParse(FixedServer?.ToString() ?? serverText, out var server, out var error))
        {
            return new PairingOutcome(false, error);
        }

        var (settings, errors) = config.TryLoad();
        if (errors.Count > 0)
        {
            return new PairingOutcome(false, "Fix the settings file first: " + string.Join("; ", errors));
        }

        try
        {
            using var pairing = _client(server);
            var paired = await pairing.PairAsync(agentVersion, showCode, ct).ConfigureAwait(false);
            credentials.Save(paired);
            if (settings.Server is null || !ServerAddress.Same(settings.Server, server))
            {
                config.Save(settings with { Server = server });
            }

            return new PairingOutcome(true, $"Paired as {paired.AgentId} with {server}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new PairingOutcome(false, "Pairing cancelled.");
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or InvalidDataException or TaskCanceledException
                                      or System.Text.Json.JsonException)
        {
            return new PairingOutcome(false, $"Pairing failed: {e.Message}");
        }
    }

    /// <summary>The address to offer: the settings file's, else the one paired last.</summary>
    public string SuggestedServer() => FixedServer?.ToString() ??
        (config.TryLoad() is ({ Server: { } s }, { Count: 0 }) ? s : credentials.Load()?.Server)?.ToString() ?? "https://";

    /// <summary>For the screen: the settings file after pairing (null when it has errors).</summary>
    public AgentConfig? Settings() => config.TryLoad() is (var c, { Count: 0 }) ? c : null;
}
