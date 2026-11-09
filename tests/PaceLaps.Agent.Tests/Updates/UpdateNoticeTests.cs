using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Updates;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.Updates;

/// <summary>The download server's two files, answered by path; records what was asked for and how.</summary>
internal sealed class FakeDownloads : HttpMessageHandler
{
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public bool Down { get; set; }

    public string Latest { get; set; } = UpdateNoticeTests.LatestJson("0.13.0");

    public string? Changelog { get; set; } = UpdateNoticeTests.ChangelogJson;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (Down)
        {
            throw new HttpRequestException("no network (fake)");
        }

        var text = request.RequestUri!.AbsolutePath switch
        {
            "/agent/latest.json" => Latest,
            "/agent/changelog.json" => Changelog,
            _ => null,
        };
        return Task.FromResult(text is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") });
    }
}

public sealed class UpdateNoticeTests
{
    // As scripts/upload-downloads.ps1 writes them.
    internal static string LatestJson(string version, int v = 1, string product = "agent") => $$"""
        {
          "v": {{v}},
          "product": "{{product}}",
          "version": "{{version}}",
          "released": "2026-11-09T12:00:00Z",
          "prerelease": true,
          "file": "pacelaps-agent-{{version}}-win-x64.zip",
          "url": "https://downloads.pacelaps.com/agent/{{version}}/pacelaps-agent-{{version}}-win-x64.zip",
          "sha256": "ab12",
          "size": 1234
        }
        """;

    internal const string ChangelogJson = """
        {
          "v": 1,
          "product": "agent",
          "releases": [
            { "version": "0.13.0", "date": "2026-11-09", "changes": ["The tray menu says when a new version is out.", "Sleep now reads: disconnect from Race Control."] },
            { "version": "0.12.0", "date": "2026-11-04", "changes": ["Class names and colours."] }
          ]
        }
        """;

    [Theory]
    [InlineData("0.12.0", "0.13.0", -1)]
    [InlineData("0.12.0+1a2b3c4", "0.12.0", 0)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-beta", "1.0.0-alpha.1", 1)]
    [InlineData("0.9.9", "0.10.0", -1)]
    public void Versions_compare_as_semver(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(ReleaseVersion.TryParse(a)!.CompareTo(ReleaseVersion.TryParse(b))));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("v1.2.3")]
    [InlineData("")]
    public void Unreadable_versions_are_null(string text) => Assert.Null(ReleaseVersion.TryParse(text));

    [Fact]
    public void A_newer_release_is_offered_with_its_changelog_lines()
    {
        var offer = UpdateCheck.Offer("0.12.0+1a2b3c4", LatestJson("0.13.0"), ChangelogJson);

        Assert.NotNull(offer);
        Assert.Equal("0.13.0", offer.Version);
        Assert.Equal(["The tray menu says when a new version is out.", "Sleep now reads: disconnect from Race Control."], offer.Lines);
    }

    [Fact]
    public void Nothing_is_offered_for_the_same_or_an_older_release_or_bad_documents()
    {
        Assert.Null(UpdateCheck.Offer("0.13.0", LatestJson("0.13.0"), ChangelogJson));
        Assert.Null(UpdateCheck.Offer("0.14.0", LatestJson("0.13.0"), ChangelogJson));
        Assert.Null(UpdateCheck.Offer("0.12.0", LatestJson("0.13.0", v: 2), ChangelogJson));
        Assert.Null(UpdateCheck.Offer("0.12.0", LatestJson("0.13.0", product: "plugin"), ChangelogJson));
        Assert.Null(UpdateCheck.Offer("0.12.0", "<html>", ChangelogJson));
        Assert.Null(UpdateCheck.Offer("unknown", LatestJson("0.13.0"), ChangelogJson));
    }

    [Fact]
    public void Early_releases_are_offered_only_to_early_agents()
    {
        Assert.Null(UpdateCheck.Offer("1.0.0", LatestJson("1.1.0-rc.1"), null));
        Assert.Equal("1.1.0-rc.1", UpdateCheck.Offer("1.1.0-rc.0", LatestJson("1.1.0-rc.1"), null)?.Version);
        Assert.Equal("1.0.0", UpdateCheck.Offer("0.13.0", LatestJson("1.0.0"), null)?.Version);
    }

    [Fact]
    public void A_missing_or_broken_changelog_still_offers_the_release()
    {
        Assert.Empty(UpdateCheck.Offer("0.12.0", LatestJson("0.13.0"), null)!.Lines);
        Assert.Empty(UpdateCheck.Offer("0.12.0", LatestJson("0.13.0"), "not json")!.Lines);
        Assert.Empty(UpdateCheck.Offer("0.12.0", LatestJson("0.14.0"), ChangelogJson)!.Lines);
    }

    [Fact]
    public async Task The_watch_checks_at_start_then_once_a_day_and_sends_nothing_about_the_agent()
    {
        var clock = new FakeClock();
        var downloads = new FakeDownloads();
        using var watch = new UpdateWatch("0.12.0+1a2b3c4", clock, () => true, NullLogger.Instance, downloads);

        await watch.CheckIfDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal("0.13.0", watch.Offer?.Version);
        Assert.Equal(2, downloads.Requests.Count);

        clock.Advance(TimeSpan.FromHours(23));
        await watch.CheckIfDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, downloads.Requests.Count);

        clock.Advance(TimeSpan.FromHours(1));
        await watch.CheckIfDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, downloads.Requests.Count);

        Assert.All(downloads.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("downloads.pacelaps.com", r.RequestUri!.Host);
            Assert.Equal("https", r.RequestUri.Scheme);
            Assert.Empty(r.RequestUri.Query);
            Assert.Null(r.Headers.Authorization);
            Assert.Equal("PaceLapsAgent", r.Headers.UserAgent.ToString());
        });
    }

    [Fact]
    public async Task A_failed_check_is_tried_again_an_hour_later_and_keeps_an_earlier_offer()
    {
        var clock = new FakeClock();
        var downloads = new FakeDownloads();
        using var watch = new UpdateWatch("0.12.0", clock, () => true, NullLogger.Instance, downloads);
        await watch.CheckIfDueAsync(TestContext.Current.CancellationToken);

        clock.Advance(UpdateWatch.Interval);
        downloads.Down = true;
        await watch.CheckIfDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal("0.13.0", watch.Offer?.Version);

        clock.Advance(TimeSpan.FromMinutes(59));
        var before = downloads.Requests.Count;
        await watch.CheckIfDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before, downloads.Requests.Count);

        clock.Advance(TimeSpan.FromMinutes(1));
        downloads.Down = false;
        downloads.Latest = LatestJson("0.14.0");
        await watch.CheckIfDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal("0.14.0", watch.Offer?.Version);
    }

    [Fact]
    public async Task Off_by_setting_or_in_a_local_build_nothing_is_fetched()
    {
        var clock = new FakeClock();
        var downloads = new FakeDownloads();
        var enabled = false;
        using var watch = new UpdateWatch("0.12.0", clock, () => enabled, NullLogger.Instance, downloads);

        await watch.CheckIfDueAsync(TestContext.Current.CancellationToken);
        Assert.Empty(downloads.Requests);
        Assert.Null(watch.Offer);

        enabled = true;
        clock.Advance(UpdateWatch.Interval);
        await watch.CheckIfDueAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(downloads.Requests);
    }

    [Fact]
    public void The_menu_line_and_one_notification_per_version_never_during_a_race()
    {
        var offer = new UpdateOffer("0.13.0", []);
        var sleeping = new AgentStatus { State = AgentState.Sleeping, AgentId = "agt_01" };

        Assert.Null(TrayPresenter.UpdateMenuText(null));
        Assert.Equal("Update available: 0.13.0...", TrayPresenter.UpdateMenuText(offer));
        Assert.Equal("Update available", TrayPresenter.UpdateNotice(offer, null, sleeping)?.Title);
        Assert.Null(TrayPresenter.UpdateNotice(offer, "0.13.0", sleeping));
        Assert.Null(TrayPresenter.UpdateNotice(offer, null, sleeping with { State = AgentState.Active }));
        Assert.Null(TrayPresenter.UpdateNotice(offer, null, sleeping with { State = AgentState.SelfTest }));
        Assert.NotNull(TrayPresenter.UpdateNotice(offer, null, sleeping with { State = AgentState.Ready }));
        Assert.Null(TrayPresenter.UpdateNotice(null, null, sleeping));
    }

    [Fact]
    public void The_setting_is_on_by_default_and_can_be_turned_off()
    {
        Assert.True(new AgentConfig().CheckForUpdates);
        var (config, errors) = AgentConfig.Parse("""{ "checkForUpdates": false }""");
        Assert.Empty(errors);
        Assert.False(config.CheckForUpdates);
        Assert.Contains("\"checkForUpdates\": false", config.ToJson());
        Assert.Contains("checkForUpdates", AgentConfig.Parse("""{ "checkForUpdates": "no" }""").Errors.Single());

        var model = SettingsModel.From(config, startWithWindows: false);
        Assert.False(model.CheckForUpdates);
        Assert.False(model.ToConfig().Config!.CheckForUpdates);
    }
}
