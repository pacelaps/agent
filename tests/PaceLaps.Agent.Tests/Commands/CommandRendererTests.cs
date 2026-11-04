using System.Text.Json;
using PaceLaps.Agent.Core.Commands;

namespace PaceLaps.Agent.Tests.Commands;

public sealed class CommandRendererTests
{
    private readonly CommandRenderer _renderer = new();

    private RenderResult R(string type, string paramsJson = "{}") => _renderer.Render(type, JsonDocument.Parse(paramsJson).RootElement);

    private string Text(string type, string paramsJson = "{}")
    {
        var r = R(type, paramsJson);
        Assert.True(r.Ok, r.Error);
        return r.Command!.ChatText!;
    }

    private void Rejected(string type, string paramsJson, string reasonPart)
    {
        var r = R(type, paramsJson);
        Assert.False(r.Ok, $"expected {type} {paramsJson} to be rejected");
        Assert.Contains(reasonPart, r.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pit_close", "!pitclose")]
    [InlineData("pit_open", "!pitopen")]
    [InlineData("restart_single", "!restart single")]
    [InlineData("restart_double", "!restart double")]
    [InlineData("clear_all", "!clearall")]
    [InlineData("yellow", "!yellow")]
    public void Fixed_commands_render_and_may_use_a_macro(string type, string expected)
    {
        var c = R(type).Command!;

        Assert.Equal(expected, c.ChatText);
        Assert.Equal(ChannelOptions.MacroOrTyped, c.Channels);
        Assert.True(c.IdempotentByCatalogue);
        Assert.True(c.SyntaxConfirmed);
    }

    [Theory]
    [InlineData("pit_close")]
    [InlineData("restart_single")]
    [InlineData("clear_all")]
    public void Fixed_commands_reject_any_parameter(string type) => Rejected(type, """{"car":"12"}""", "parameter 'car' is not allowed");

    [Fact]
    public void Yellow_with_a_message_is_typed_only()
    {
        var c = R("yellow", """{"message":" Debris in turn 3 "}""").Command!;

        Assert.Equal("!yellow Debris in turn 3", c.ChatText);
        Assert.Equal(ChannelOptions.TypedOnly, c.Channels);
    }

    [Theory]
    [InlineData("""{"message":""}""", "must not be empty")]
    [InlineData("""{"message":"line\nbreak"}""", "not allowed")]
    [InlineData("""{"message":"emoji 🏁"}""", "not allowed")]
    [InlineData("""{"message":42}""", "must be a string")]
    [InlineData("""{"text":"x"}""", "parameter 'text' is not allowed")]
    public void Yellow_message_is_validated(string json, string reason) => Rejected("yellow", json, reason);

    [Fact]
    public void Free_text_may_use_letters_of_any_language()
    {
        Assert.Equal("!yellow Ölspur in Kurve 3, bitte langsam!", Text("yellow", """{"message":"Ölspur in Kurve 3, bitte langsam!"}"""));
    }

    [Fact]
    public void Clear_renders_with_the_car_number_and_takes_no_message()
    {
        Assert.Equal("!clear #12", Text("clear", """{"car":"12"}"""));
        Assert.Equal("!clear #007", Text("clear", """{"car":"007"}"""));
        Rejected("clear", """{"car":"12","message":"x"}""", "parameter 'message' is not allowed");
    }

    [Theory]
    [InlineData("""{}""", "parameter 'car' is required")]
    [InlineData("""{"car":"1234"}""", "one to three digits")]
    [InlineData("""{"car":"12a"}""", "one to three digits")]
    [InlineData("""{"car":"#12"}""", "one to three digits")]
    [InlineData("""{"car":"12 !dq #5"}""", "one to three digits")]
    [InlineData("""{"car":12}""", "must be a string")]
    public void Car_numbers_are_validated(string json, string reason) => Rejected("clear", json, reason);

    [Theory]
    [InlineData("wave_by", "!waveby #4", false, true)]
    [InlineData("eol", "!eol #4", false, true)]
    [InlineData("dq", "!dq #4", false, false)]
    [InlineData("remove", "!remove #4", false, false)]
    public void Car_commands_with_an_optional_message(string type, string expected, bool idempotent, bool confirmed)
    {
        var c = R(type, """{"car":"4"}""").Command!;
        Assert.Equal(expected, c.ChatText);
        Assert.Equal(ChannelOptions.TypedOnly, c.Channels);
        Assert.Equal(idempotent, c.IdempotentByCatalogue);
        Assert.Equal(confirmed, c.SyntaxConfirmed);

        Assert.Equal(expected + " Lapped car, go around", Text(type, """{"car":"4","message":"Lapped car, go around"}"""));
    }

    [Theory]
    [InlineData("""{"n":4}""", "pace_laps_set", "!pacelaps 4", ChannelOptions.MacroOrTyped, true)]
    [InlineData("""{"n":2}""", "pace_laps_add", "!pacelaps +2", ChannelOptions.TypedOnly, false)]
    public void Pace_laps(string json, string type, string expected, ChannelOptions channels, bool idempotent)
    {
        var c = R(type, json).Command!;

        Assert.Equal(expected, c.ChatText);
        Assert.Equal(channels, c.Channels);
        Assert.Equal(idempotent, c.IdempotentByCatalogue);
    }

    [Theory]
    [InlineData("""{"n":0}""", "n must be 1 to 20")]
    [InlineData("""{"n":21}""", "n must be 1 to 20")]
    [InlineData("""{"n":2.5}""", "whole number")]
    [InlineData("""{"n":"3"}""", "whole number")]
    [InlineData("""{}""", "parameter 'n' is required")]
    public void Pace_laps_are_validated(string json, string reason) => Rejected("pace_laps_set", json, reason);

    [Fact]
    public void Pace_lap_limit_comes_from_the_limits()
    {
        var renderer = new CommandRenderer(new CommandLimits { MaxPaceLaps = 50 });

        Assert.True(renderer.Render("pace_laps_set", JsonDocument.Parse("""{"n":50}""").RootElement).Ok);
    }

    [Fact]
    public void Black_flag_modes()
    {
        Assert.Equal("!black #3 D", Text("black_flag", """{"car":"3","mode":"drive_through"}"""));
        Assert.Equal("!black #3 10", Text("black_flag", """{"car":"3","mode":"stop_and_go","seconds":10}"""));
        Assert.False(R("black_flag", """{"car":"3","mode":"drive_through"}""").Command!.IdempotentByCatalogue);
    }

    [Theory]
    [InlineData("""{"car":"3","mode":"stop_and_go"}""", "stop_and_go needs seconds")]
    [InlineData("""{"car":"3","mode":"stop_and_go","seconds":0}""", "seconds must be 1 to 300")]
    [InlineData("""{"car":"3","mode":"drive_through","seconds":10}""", "not allowed for a drive-through")]
    [InlineData("""{"car":"3","mode":"stop_and_go","laps":2}""", "not confirmed")]
    [InlineData("""{"car":"3","mode":"slow_down"}""", "mode must be")]
    [InlineData("""{"car":"3"}""", "parameter 'mode' is required")]
    public void Black_flag_is_validated(string json, string reason) => Rejected("black_flag", json, reason);

    [Fact]
    public void Announce_is_plain_chat()
    {
        var c = R("announce", """{"text":"Race control: green flag next lap"}""").Command!;

        Assert.Equal("Race control: green flag next lap", c.ChatText);
        Assert.Equal(ChannelOptions.TypedOnly, c.Channels);
        Assert.False(c.IdempotentByCatalogue);
    }

    [Theory]
    [InlineData("""{"text":"!dq #12"}""", "must not start with ! or /")]
    [InlineData("""{"text":" /all hi"}""", "must not start with ! or /")]
    [InlineData("""{"text":"tab\there"}""", "not allowed")]
    [InlineData("""{}""", "parameter 'text' is required")]
    public void Announce_cannot_become_a_command(string json, string reason) => Rejected("announce", json, reason);

    [Fact]
    public void Free_text_respects_the_chat_length_limit_including_the_command()
    {
        var renderer = new CommandRenderer(new CommandLimits { MaxChatLength = 30 });
        var p = (string json) => JsonDocument.Parse(json).RootElement;

        Assert.True(renderer.Render("announce", p("""{"text":"123456789012345678901234567890"}""")).Ok);
        Assert.False(renderer.Render("announce", p("""{"text":"1234567890123456789012345678901"}""")).Ok);

        // "!yellow " is 8 characters, so 22 are left for the message.
        Assert.True(renderer.Render("yellow", p("""{"message":"1234567890123456789012"}""")).Ok);
        var tooLong = renderer.Render("yellow", p("""{"message":"12345678901234567890123"}"""));
        Assert.Contains("would be 31 characters, the limit is 30", tooLong.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_over_230_characters_is_rejected_even_if_the_line_would_fit()
    {
        Rejected("eol", $$"""{"car":"12","message":"{{new string('y', 231)}}"}""", "message is 231 characters, the limit is 230");
        Assert.True(R("eol", $$"""{"car":"12","message":"{{new string('y', 230)}}"}""").Ok);
    }

    /// <summary>Every command the contract rejects, the agent rejects too (examples/invalid in contracts/).</summary>
    [Fact]
    public void Every_invalid_command_example_of_the_contract_is_rejected()
    {
        var files = Directory.GetFiles(Path.Combine(PaceLaps.Agent.Tests.Contracts.ContractSchemas.Folder, "examples", "invalid"), "command.*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var doc = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            var type = doc.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var parameters = doc.TryGetProperty("params", out var p) ? p : default;
            Assert.False(_renderer.Render(type, parameters).Ok, $"{Path.GetFileName(file)} was accepted");
        }
    }

    [Fact]
    public void Replay_jump_uses_the_sdk()
    {
        var c = R("replay_jump", """{"sessionNum":2,"sessionTime":812.4}""").Command!;

        Assert.Null(c.ChatText);
        Assert.Equal(ChannelOptions.Sdk, c.Channels);
        Assert.Equal(new ReplayTarget(2, 812.4), c.Replay);
        Rejected("replay_jump", """{"sessionNum":-1,"sessionTime":1}""", "must not be negative");
    }

    [Fact]
    public void Replay_jump_can_follow_a_car_and_replay_live_takes_no_params()
    {
        var jump = R("replay_jump", """{"sessionNum":2,"sessionTime":812.4,"car":"018"}""").Command!;
        Assert.Equal(new ReplayTarget(2, 812.4, "018"), jump.Replay);
        Assert.Equal("018", jump.Car);
        Rejected("replay_jump", """{"sessionNum":2,"sessionTime":1,"car":"1234"}""", "one to three digits");

        var live = R("replay_live", "{}").Command!;
        Assert.Equal(ChannelOptions.Sdk, live.Channels);
        Assert.Null(live.Replay);
        Assert.Null(live.ChatText);
        Rejected("replay_live", """{"car":"7"}""", "car");
        Assert.Equal(["replay_jump", "replay_live"], CommandRenderer.ViewTypes.Order());
    }

    [Fact]
    public void Unknown_types_and_non_object_params_are_rejected()
    {
        Rejected("kick", "{}", "unknown command type 'kick'");
        Rejected("yellow", "[]", "params must be an object");
    }

    [Fact]
    public void Missing_params_count_as_empty()
    {
        Assert.Equal("!pitclose", _renderer.Render("pit_close", default).Command!.ChatText);
    }

    [Fact]
    public void Every_known_type_has_a_rule()
    {
        foreach (var type in CommandRenderer.KnownTypes)
        {
            var r = _renderer.Render(type, JsonDocument.Parse("{}").RootElement);
            Assert.DoesNotContain("unknown command type", r.Error ?? "", StringComparison.Ordinal);
        }
    }
}
