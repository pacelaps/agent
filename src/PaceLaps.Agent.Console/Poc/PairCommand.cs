using PaceLaps.Agent.ConsoleHost.Cli;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.ConsoleHost.Spikes;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.Time;

namespace PaceLaps.Agent.ConsoleHost.Poc;

/// <summary>
/// <c>pair --server URL</c>: device-code pairing (spec: Identity and access &gt; Agents). The operator approves the
/// code on the web page while signed in as a steward; the token is stored encrypted for this Windows user.
/// </summary>
internal static class PairCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var server = ParseServer(args.Require("server"));
        var store = new CredentialStore(CredentialStore.DefaultPath);
        if (store.Load() is { } existing && !args.Has("again"))
        {
            Console.WriteLine($"Already paired as {existing.AgentId} with {existing.Server}. Use --again to pair anew.");
            return 0;
        }

        using var client = new PairingClient(server, new SystemClock());
        var credentials = await client.PairAsync(SpikeEnvironment.AgentVersion, code =>
        {
            Console.WriteLine();
            Console.WriteLine($"  Open {code.VerificationUrl}");
            Console.WriteLine($"  sign in as a steward and enter the code  {code.UserCode}");
            Console.WriteLine(code.ExpiresAt is { } until ? $"  The code is valid until {until.ToLocalTime():HH:mm}." : "");
            Console.WriteLine("  Waiting for the approval...");
        }, ct);

        store.Save(credentials);
        Console.WriteLine($"Paired as {credentials.AgentId}. Start the agent with: run --server {server}");
        return 0;
    }

    /// <summary>The server URL: HTTPS, or plain HTTP for this PC only (development).</summary>
    public static Uri ParseServer(string raw) => ServerAddress.TryParse(raw, out var url, out var error) ? url : throw new CliException("--server: " + error);
}
