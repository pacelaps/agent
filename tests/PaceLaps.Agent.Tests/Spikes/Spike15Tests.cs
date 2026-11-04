using PaceLaps.Agent.ConsoleHost.Spikes;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.Tests.Spikes;

public sealed class Spike15Tests
{
    [Fact]
    public void Finds_car_numbers_that_start_with_another()
    {
        string[] cars = ["7", "77", "777", "12", "1", "10", "218", "2"];

        Assert.Equal(["77", "777"], Spike15ChatEnter.PrefixOf("7", cars));
        Assert.Equal(["12", "10"], Spike15ChatEnter.PrefixOf("1", cars));
        Assert.Empty(Spike15ChatEnter.PrefixOf("12", cars));
        Assert.Equal(["218"], Spike15ChatEnter.PrefixOf("2", cars));
    }

    [Fact]
    public void Summarizes_effects_and_left_over_lines_per_car_and_form()
    {
        Spike15Send S(string car, string variant, bool effect, string? left) =>
            new(car, variant, 1, "!black #" + car + " D", true, effect, effect ? 0.2 : null, left is null ? ReadbackOutcome.NothingCopied : ReadbackOutcome.Text, left);

        var cases = Spike15ChatEnter.Summarize([S("7", "plain", false, "!black #7 D"), S("7", "plain", true, null), S("7", "pause100", true, null), S("12", "plain", true, null)]);

        Assert.Equal(new Spike15Case("7", "plain", 2, 1, 1), cases[0]);
        Assert.Equal(new Spike15Case("7", "pause100", 1, 1, 0), cases[1]);
        Assert.Equal(new Spike15Case("12", "plain", 1, 1, 0), cases[2]);
    }

    [Theory]
    [InlineData(ChatReadback.Sentinel, ReadbackOutcome.NothingCopied, null)]
    [InlineData(null, ReadbackOutcome.NothingCopied, null)]
    [InlineData("!eol #7", ReadbackOutcome.Text, "!eol #7")]
    public void Interprets_the_clipboard_after_the_copy(string? clipboard, ReadbackOutcome outcome, string? text)
    {
        var r = ChatReadback.Interpret(clipboard);

        Assert.Equal(outcome, r.Outcome);
        Assert.Equal(text, r.Text);
    }
}
