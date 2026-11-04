using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Time;

namespace PaceLaps.Agent.Core.Server;

/// <summary>What pairing gives the agent: its server-assigned id and its token.</summary>
public sealed record AgentCredentials(Uri Server, string AgentId, string Token);

/// <summary>
/// Device-code pairing (contracts/agent-pairing.schema.json; spec: Identity and access &gt; Agents): the agent asks
/// for a code, the operator approves it on the web page, the agent polls until it gets its token.
/// </summary>
public sealed class PairingClient(Uri server, IClock clock, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler(), disposeHandler: true) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(15) };

    /// <param name="showCode">Shown to the operator: the code and where to enter it.</param>
    public async Task<AgentCredentials> PairAsync(string agentVersion, Action<PairingCodeMessage> showCode, CancellationToken ct)
    {
        var code = await PostAsync<PairingCodeMessage>("api/v1/agent/pairing", new PairingRequestMessage { AgentVersion = agentVersion }, ct).ConfigureAwait(false);
        showCode(code);
        var interval = TimeSpan.FromSeconds(Math.Max(1, code.IntervalSec));
        while (true)
        {
            await clock.Delay(interval, ct).ConfigureAwait(false);
            var answer = await PostAsync<PairingAnswerMessage>("api/v1/agent/pairing/token", new PairingPollMessage { PairingId = code.PairingId }, ct).ConfigureAwait(false);
            if (answer.Kind == "pairing_token" && answer.AgentId is { } agentId && answer.Token is { } token)
            {
                return new AgentCredentials(server, agentId, token);
            }

            switch (answer.Status)
            {
                case "denied":
                    throw new InvalidOperationException("The pairing was denied on the web page.");
                case "expired":
                    throw new InvalidOperationException("The pairing code expired. Run pair again.");
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    break;
            }

            if (answer.IntervalSec is > 0 and var seconds)
            {
                interval = TimeSpan.FromSeconds(Math.Max(seconds, interval.TotalSeconds));
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<T> PostAsync<T>(string path, object message, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(message, message.GetType(), ContractJson.Options), Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PostAsync(path, content, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{path}: HTTP {(int)response.StatusCode} {(text.Length > 200 ? text[..200] : text)}");
        }

        return JsonSerializer.Deserialize<T>(text, ContractJson.Options) ?? throw new InvalidDataException($"{path}: empty answer");
    }
}
