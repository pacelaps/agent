using System.Text.Json;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.LocalFailsafe;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Tests.Contracts;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.LocalFailsafe;

public sealed class LocalFailsafeCopiesTests
{
    private static readonly string H1 = new('1', 64);
    private static readonly string H2 = new('2', 64);
    private readonly FakeClock _clock = new();
    private readonly FakeServer _server;
    private readonly MemoryLocalFailsafeStore _store = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public LocalFailsafeCopiesTests() => _server = new FakeServer(_clock);

    internal static JsonElement TemplateDocument() => JsonDocument.Parse(ContractSchemas.Example("procedure.local-failsafe.json")).RootElement.Clone();

    private static ServerView Names(LocalFailsafeTarget? target) => new ServerView(true, "s_1", "Race 1", 1, true) { LocalFailsafeKnown = true, LocalFailsafe = target };

    private static LocalFailsafeTarget Target(int version, string hash, bool armPractice = false) => new("local-failsafe-default", version, hash, armPractice, false);

    private static LocalFailsafeDocumentMessage Document(int version, string hash, JsonElement? doc = null) => new()
    {
        Procedure = "local-failsafe-default", Version = version, Hash = hash, Document = doc ?? TemplateDocument(),
    };

    [Fact]
    public async Task A_named_version_is_fetched_checked_and_stored()
    {
        var copies = new LocalFailsafeCopies(_server, _store, _clock);
        _server.LocalFailsafeDocument = Document(1, H1);

        await copies.FollowAsync(Names(Target(1, H1)), _ct);
        await copies.FollowAsync(Names(Target(1, H1)), _ct);

        Assert.Equal(1, _server.FetchCount);
        Assert.Equal((1, H1), (copies.Held!.Version, copies.Held.Hash));
        Assert.Same(copies.Held, _store.Copy);
        Assert.NotNull(copies.Held.Read());
    }

    [Fact]
    public async Task The_copy_outlives_a_restart()
    {
        _server.LocalFailsafeDocument = Document(1, H1);
        await new LocalFailsafeCopies(_server, _store, _clock).FollowAsync(Names(Target(1, H1)), _ct);

        var afterRestart = new LocalFailsafeCopies(_server, _store, _clock);

        Assert.Equal(1, afterRestart.Held!.Version);
    }

    [Fact]
    public async Task A_copy_that_cannot_run_offline_is_not_kept_and_the_fetch_is_tried_again_after_a_minute()
    {
        var copies = new LocalFailsafeCopies(_server, _store, _clock);
        _server.LocalFailsafeDocument = Document(1, H1);
        await copies.FollowAsync(Names(Target(1, H1)), _ct);
        var bad = JsonDocument.Parse("""{"id": "x", "phases": [{"id": "a", "blocks": [{"type": "wait_steward", "params": {}}]}]}""").RootElement.Clone();
        _server.LocalFailsafeDocument = Document(2, H2, bad);

        await copies.FollowAsync(Names(Target(2, H2)), _ct);
        await copies.FollowAsync(Names(Target(2, H2)), _ct);

        Assert.Equal(1, copies.Held!.Version);
        Assert.Equal(2, _server.FetchCount);
        _clock.Advance(LocalFailsafeCopies.RetryAfter);
        _server.LocalFailsafeDocument = Document(2, H2);
        await copies.FollowAsync(Names(Target(2, H2)), _ct);
        Assert.Equal(2, copies.Held!.Version);
    }

    [Fact]
    public async Task A_document_of_another_version_than_named_is_not_kept()
    {
        var copies = new LocalFailsafeCopies(_server, _store, _clock);
        _server.LocalFailsafeDocument = Document(3, H2);

        await copies.FollowAsync(Names(Target(2, H2)), _ct);

        Assert.Null(copies.Held);
    }

    [Fact]
    public async Task None_designated_deletes_the_copy_and_an_answer_that_says_nothing_changes_nothing()
    {
        var copies = new LocalFailsafeCopies(_server, _store, _clock);
        _server.LocalFailsafeDocument = Document(1, H1);
        await copies.FollowAsync(Names(Target(1, H1)), _ct);

        await copies.FollowAsync(new ServerView(true, "s_1", "Race 1", 1, true), _ct);
        Assert.NotNull(copies.Held);

        await copies.FollowAsync(Names(null), _ct);
        Assert.Null(copies.Held);
        Assert.Null(_store.Copy);
    }

    [Fact]
    public async Task A_change_of_the_leagues_arming_updates_the_copy_without_a_fetch()
    {
        var copies = new LocalFailsafeCopies(_server, _store, _clock);
        _server.LocalFailsafeDocument = Document(1, H1);
        await copies.FollowAsync(Names(Target(1, H1)), _ct);

        await copies.FollowAsync(Names(Target(1, H1, armPractice: true)), _ct);

        Assert.True(copies.Held!.ArmPractice);
        Assert.Equal(1, _server.FetchCount);
    }
}
