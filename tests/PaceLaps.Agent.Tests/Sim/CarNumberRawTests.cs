using PaceLaps.Agent.Core.Sim;

namespace PaceLaps.Agent.Tests.Sim;

public sealed class CarNumberRawTests
{
    [Theory]
    [InlineData("7", 7)]
    [InlineData("12", 12)]
    [InlineData("218", 218)]
    [InlineData("0", 0)]
    [InlineData("07", 2007)]
    [InlineData("007", 3007)]
    [InlineData("012", 3012)]
    public void Keeps_leading_zeros_as_iRacing_does(string number, int raw) => Assert.Equal(raw, CarNumberRaw.From(number));

    [Theory]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("1a")]
    [InlineData("-1")]
    public void Refuses_what_is_not_a_car_number(string number) => Assert.Throws<ArgumentException>(() => CarNumberRaw.From(number));
}
