using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.LocalFailsafe;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.Time;

namespace PaceLaps.Agent.Tests.Server;

/// <summary>
/// Against a running web service (development stack), skipped unless IRC_LIVE_URL, IRC_LIVE_AGENT and
/// IRC_LIVE_TOKEN are set: the real HTTP and websocket protocol, which the fakes cannot prove. With
/// IRC_LIVE_LEAGUE (default 4242) the agent reports a hosted session of that league; the league needs an open
/// session. It waits up to IRC_LIVE_WAIT seconds (default 0) for a command, so one can be sent meanwhile.
/// </summary>
public sealed class LiveServerTests
{
    [Fact]
    public async Task Speaks_the_agent_api_and_the_websocket_with_a_real_server()
    {
        var url = Environment.GetEnvironmentVariable("IRC_LIVE_URL");
        var agentId = Environment.GetEnvironmentVariable("IRC_LIVE_AGENT");
        var token = Environment.GetEnvironmentVariable("IRC_LIVE_TOKEN");
        Assert.SkipWhen(url is null || agentId is null || token is null, "IRC_LIVE_URL, IRC_LIVE_AGENT and IRC_LIVE_TOKEN are not set");
        var league = int.Parse(Environment.GetEnvironmentVariable("IRC_LIVE_LEAGUE") ?? "4242");
        var wait = int.Parse(Environment.GetEnvironmentVariable("IRC_LIVE_WAIT") ?? "0");
        var ct = TestContext.Current.CancellationToken;

        // Generous timeouts: a development stack on a Windows bind mount can take seconds per request.
        await using var link = new HttpServerLink(new HttpServerLinkOptions { BaseUrl = new Uri(url!), AgentId = agentId!, Token = token!, RequestTimeout = TimeSpan.FromSeconds(30) }, new SystemClock());
        var commands = new List<CommandMessage>();
        var views = new List<ServerView>();
        link.CommandReceived += commands.Add;
        link.ViewReceived += views.Add;
        await link.StartAsync(ct);

        var identity = new SessionIdentity(league, 1, 0, 0, 0, "Practice");
        Assert.True((await link.CheckSessionAsync(identity, 0, ct)).Ok);
        var hello = await link.HelloAsync(new HelloMessage { AgentId = agentId!, State = AgentState.Ready, AgentVersion = "live-test", Session = identity.ToContract() }, ct);
        Assert.True(hello.Active, hello.Reason);
        var pong = await link.PingAsync(new ClockPingMessage { ClientSentAt = DateTimeOffset.UtcNow }, ct);
        Assert.NotNull(pong?.ServerReceivedAt);

        var until = DateTime.UtcNow.AddSeconds(30);
        while (!link.IsRealtime && DateTime.UtcNow < until)
        {
            await Task.Delay(100, ct);
        }

        Assert.True(link.IsRealtime, "the websocket did not subscribe to the agent channel");
        await link.DeliverAsync(new HeartbeatMessage { AgentId = agentId!, Epoch = hello.Epoch, State = AgentState.Active, InSession = true }, ct);
        // The answer (an executor assignment may come too: the server fills an empty executor).
        Assert.Equal(true, Assert.Single(views, v => v.Active is not null).Active);

        until = DateTime.UtcNow.AddSeconds(wait);
        while (commands.Count == 0 && DateTime.UtcNow < until)
        {
            await link.DeliverAsync(new HeartbeatMessage { AgentId = agentId!, Epoch = hello.Epoch, State = AgentState.Active, InSession = true }, ct);
            await Task.Delay(1000, ct);
        }

        if (wait > 0)
        {
            var command = Assert.Single(commands);
            await link.DeliverAsync(new CommandStatusMessage { CommandId = command.Id, Step = CommandStep.ReceivedByExecutor, At = DateTimeOffset.UtcNow }, ct);
            await link.DeliverAsync(new CommandStatusMessage { CommandId = command.Id, Step = CommandStep.Result, Outcome = CommandOutcome.Sent, At = DateTimeOffset.UtcNow, Detail = "live test" }, ct);
            Console.WriteLine($"LIVE command {command.Id} {command.Type} epoch {command.Epoch}");
        }
    }

    [Fact]
    public async Task Speaks_the_step_5_additions_with_a_real_server()
    {
        var url = Environment.GetEnvironmentVariable("IRC_LIVE_URL");
        var agentId = Environment.GetEnvironmentVariable("IRC_LIVE_AGENT");
        var token = Environment.GetEnvironmentVariable("IRC_LIVE_TOKEN");
        Assert.SkipWhen(url is null || agentId is null || token is null, "IRC_LIVE_URL, IRC_LIVE_AGENT and IRC_LIVE_TOKEN are not set");
        var league = int.Parse(Environment.GetEnvironmentVariable("IRC_LIVE_LEAGUE") ?? "4242");
        var ct = TestContext.Current.CancellationToken;
        await using var link = new HttpServerLink(new HttpServerLinkOptions { BaseUrl = new Uri(url!), AgentId = agentId!, Token = token!, RequestTimeout = TimeSpan.FromSeconds(30) }, new SystemClock());
        var views = new List<ServerView>();
        link.ViewReceived += views.Add;
        await link.StartAsync(ct);
        var identity = new SessionIdentity(league, 1, 0, 0, 0, "Practice");
        Assert.True((await link.CheckSessionAsync(identity, 0, ct)).Ok);

        // A failed self-test (the server audits it; checked on the server side, see the step 5 notes).
        await link.DeliverAsync(new SelfTestResultMessage
        {
            AgentId = agentId!, At = DateTimeOffset.UtcNow, Passed = false,
            Steps = [new SelfTestStepReport(1, SelfTestStepName.Sim, SelfTestStepResult.Pass), new SelfTestStepReport(5, SelfTestStepName.TypedChannel, SelfTestStepResult.Fail, "live test")],
            Channels = new ChannelSet(ChannelReadiness.Disabled, ChannelReadiness.Failed, ChannelReadiness.Disabled),
        }, ct);
        Assert.True(link.IsOnline);

        var hello = await link.HelloAsync(new HelloMessage { AgentId = agentId!, State = AgentState.Ready, AgentVersion = "live-test", Session = identity.ToContract() }, ct);
        Assert.True(hello.Active, hello.Reason);
        Assert.NotNull(hello.Macros);
        Assert.True(hello.LocalFailsafeKnown);

        await link.DeliverAsync(new HeartbeatMessage
        {
            AgentId = agentId!, Epoch = hello.Epoch, State = AgentState.Active, InSession = true, LocalFailsafe = LocalFailsafeState.None(enabled: true),
        }, ct);
        var view = Assert.Single(views, v => v.Active is not null);
        Assert.NotNull(view.LocalFailsafeRank);
        Assert.False(view.ReleaseLocalFailsafe);

        await link.DeliverAsync(new ResyncMessage { LastAckedSeq = 0, Outage = new OutageWindow(DateTimeOffset.UtcNow.AddSeconds(-40), DateTimeOffset.UtcNow.AddSeconds(-2)), Events = [] }, ct);
        Assert.True(link.IsOnline);

        // The copy the hello named: fetched, checked (that version, runs offline) and kept, as the agent does it.
        var copies = new LocalFailsafeCopies(link, new MemoryLocalFailsafeStore(), new SystemClock());
        await copies.FollowAsync(hello, ct);
        if (hello.LocalFailsafe is { } named)
        {
            Assert.Equal((named.Version, named.Hash), (copies.Held?.Version, copies.Held?.Hash));
            Assert.NotNull(copies.Held!.Read());
        }
        else
        {
            Assert.Null(copies.Held);
        }

        // Sleep: the last heartbeat says so, and the server takes it (and releases the agent).
        Assert.True(await link.SendNowAsync(new HeartbeatMessage { AgentId = agentId!, Epoch = hello.Epoch, State = AgentState.Sleeping, InSession = false }, ct));

        Console.WriteLine($"LIVE macros {hello.Macros}, local failsafe {hello.LocalFailsafe?.Procedure} v{hello.LocalFailsafe?.Version} (copy {(copies.Held is null ? "none" : "kept")}), rank {view.LocalFailsafeRank}");
    }
}
