using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.IRacing.Sdk;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.SelfTest;

public sealed class AppIniMacroConfigTests
{
    // As spike 10 found it in app.ini (value padded, tab, comment).
    private static readonly string[] AppIni =
    [
        "[Graphics]",
        "AutoChatStr5=not in the section",
        "[Autochat Messages]",
        "AutoChatStr1=Good luck everyone!            \t; Auto chat message",
        "AutoChatStr5=spike10markerB$            \t; Auto chat message",
        "AutoChatStr14=!pitclose$\t; Auto chat message",
        "AutoChatStr15=a;b$  ; Auto chat message",
        "AutoChatStr16=out of range",
        "[Next]",
        "AutoChatStr2=also not in the section",
    ];

    [Fact]
    public void Reads_the_slots_of_the_autochat_section_without_padding_and_comment()
    {
        var slots = AppIniMacroConfig.Parse(AppIni)!;

        Assert.Equal("Good luck everyone!", slots[1]);
        Assert.Equal("spike10markerB$", slots[5]);
        Assert.Equal("!pitclose$", slots[14]);
        Assert.Equal("a;b$", slots[15]);
        Assert.False(slots.ContainsKey(2));
        Assert.False(slots.ContainsKey(16));
    }

    [Fact]
    public void A_file_without_the_section_or_missing_file_reads_as_unreadable()
    {
        Assert.Null(AppIniMacroConfig.Parse(["[Graphics]", "x=1"]));
        Assert.Null(new AppIniMacroConfig(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini")).ReadSlots());
    }
}

public sealed class SelfTestMacroAndAdminTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeDesktop _desktop;
    private readonly FakeSim _sim = new();
    private readonly FakeServer _server;

    public SelfTestMacroAndAdminTests()
    {
        _desktop = new FakeDesktop(_clock);
        _server = new FakeServer(_clock);
    }

    private sealed class FakeMacroConfig(IReadOnlyDictionary<int, string>? slots) : IMacroConfigSource
    {
        public IReadOnlyDictionary<int, string>? ReadSlots() => slots;

        public string Location => "app.ini";
    }

    private Task<SelfTestReport> Run(FakePrompt prompt, IMacroConfigSource? config, bool poster = false) =>
        new SelfTestRunner(_sim, _server, new TypedChatSender(_desktop, _desktop, _desktop, _clock, poster: poster ? _desktop : null),
            new MacroTable([(14, "!pitclose$"), (15, "!pitopen$")]),
            prompt, new ClockOffsetEstimator(), _clock, new SelfTestOptions { MacroEnabled = true }, config).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_typed_check_uses_the_focus_way_even_where_lines_go_without_it()
    {
        var report = await Run(new FakePrompt(true, true), new FakeMacroConfig(new Dictionary<int, string> { [14] = "!pitclose$", [15] = "!pitopen$" }), poster: true);

        Assert.Equal(ChannelReadiness.Ready, report.Channels.Typed);
        Assert.Contains("front:sim", _desktop.Calls);
        Assert.DoesNotContain("post-enter", _desktop.Calls);
    }

    [Fact]
    public async Task Macro_slots_matching_app_ini_are_ready_without_asking()
    {
        var prompt = new FakePrompt(true, true);

        var report = await Run(prompt, new FakeMacroConfig(new Dictionary<int, string> { [14] = "!pitclose$", [15] = "!pitopen$", [1] = "gl" }));

        Assert.Equal(ChannelReadiness.Ready, report.Channels.Macro);
        Assert.Equal(2, prompt.Questions.Count);
        Assert.Contains("match app.ini", report.Steps[5].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mismatching_slot_leaves_the_macro_channel_unverified()
    {
        var report = await Run(new FakePrompt(true, true), new FakeMacroConfig(new Dictionary<int, string> { [14] = "!pitclose$", [15] = "my own macro" }));

        Assert.True(report.Passed);
        Assert.Equal(ChannelReadiness.Unverified, report.Channels.Macro);
        Assert.Contains("slot 15 is 'my own macro', expected '!pitopen$'", report.Steps[5].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_configuration_falls_back_to_the_operator()
    {
        var prompt = new FakePrompt(true, true, true);

        var report = await Run(prompt, new FakeMacroConfig(null));

        Assert.Equal(ChannelReadiness.Ready, report.Channels.Macro);
        Assert.Contains("Do these macro slots contain exactly this text?", prompt.Questions[^1], StringComparison.Ordinal);
    }
}
