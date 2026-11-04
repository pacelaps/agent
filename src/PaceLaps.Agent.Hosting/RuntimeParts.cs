using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.LocalFailsafe;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.IRacing.Sdk;
using PaceLaps.Agent.IRacing.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaceLaps.Agent.Hosting;

/// <summary>The connection to the web service for one activation: opened on Activate, closed on Sleep.</summary>
public interface IServerConnection : IAsyncDisposable
{
    IServerLink Link { get; }

    /// <summary>Reads the realtime key and opens the websocket and the request lanes.</summary>
    Task StartAsync(CancellationToken ct);

    bool IsOnline { get; }

    bool IsRealtime { get; }
}

/// <summary>The web service over HTTP and Reverb (<see cref="HttpServerLink"/>).</summary>
public sealed class HttpServerConnection(HttpServerLink http, Func<IServerLink, IServerLink>? wrap = null) : IServerConnection
{
    public HttpServerLink Http { get; } = http;

    public IServerLink Link { get; } = wrap?.Invoke(http) ?? http;

    public bool IsOnline => Http.IsOnline;

    public bool IsRealtime => Http.IsRealtime;

    public Task StartAsync(CancellationToken ct) => Http.StartAsync(ct);

    public ValueTask DisposeAsync() => Http.DisposeAsync();
}

/// <summary>What the agent runs on: the sim, its channels, the operator prompt and how to reach the server.</summary>
public sealed record RuntimeParts
{
    public required ISimData Sim { get; init; }

    public required TypedChatSender Typed { get; init; }

    public IMacroChannel? Macro { get; init; }

    public IReplayChannel? Replay { get; init; }

    /// <summary>The sim's app.ini for the macro check (self-test step 6), from its path (null: the default location).</summary>
    public required Func<string?, IMacroConfigSource> MacroConfig { get; init; }

    public required IOperatorPrompt Prompt { get; init; }

    public required Func<AgentCredentials, IServerConnection> Connect { get; init; }

    public required ILocalFailsafeStore LocalFailsafeStore { get; init; }

    public required IClock Clock { get; init; }

    public ILoggerFactory Loggers { get; init; } = NullLoggerFactory.Instance;

    /// <summary>Runs the agent's loop on the thread pool; tests turn it off and tick the agent themselves.</summary>
    public bool RunLoop { get; init; } = true;

    /// <summary>
    /// The real thing: the SDK connection, Win32 focus and scancode keyboard, typed lines posted to the sim's window
    /// without the focus (spec-feedback 37; the settings file can turn it off), chat readback after every line typed
    /// the focus way (spike 15, unless <paramref name="chatCheck"/> is off) and the web service over HTTP.
    /// </summary>
    public static RuntimeParts ForIRacing(SimConnection sim, Win32WindowFocus focus, ScancodeKeyboard keyboard, IOperatorPrompt prompt,
        IClock clock, ILoggerFactory loggers, TypedChatOptions? typedOptions = null, bool chatCheck = true,
        Func<AgentCredentials, IServerConnection>? connect = null)
    {
        var chatBox = chatCheck ? new ChatReadback(focus, keyboard, sim, clock) : null;
        return new RuntimeParts
        {
            Sim = new SimDataAdapter(sim),
            Typed = new TypedChatSender(focus, keyboard, sim, clock, typedOptions ?? new TypedChatOptions(), loggers.CreateLogger<TypedChatSender>(), chatBox,
                new PostedChatInput()),
            Macro = new SdkMacroChannel(sim),
            Replay = new SdkReplayChannel(sim),
            MacroConfig = path => new AppIniMacroConfig(path ?? AppIniMacroConfig.DefaultPath),
            Prompt = prompt,
            Connect = connect ?? (credentials => new HttpServerConnection(new HttpServerLink(new HttpServerLinkOptions
            {
                BaseUrl = credentials.Server,
                AgentId = credentials.AgentId,
                Token = credentials.Token,
            }, clock, loggers.CreateLogger<HttpServerLink>()))),
            LocalFailsafeStore = new LocalFailsafeFileStore(LocalFailsafeFileStore.DefaultPath),
            Clock = clock,
            Loggers = loggers,
        };
    }
}
