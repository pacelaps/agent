using System.Globalization;

namespace PaceLaps.Agent.Core.Sim;

/// <summary>
/// iRacing's CarNumberRaw for a car number as shown ("7", "07", "007"), which the SDK's camera switch takes: leading
/// zeros are kept by adding 1000 times the number of digits shown (irsdk's padCarNum): "7" is 7, "07" is 2007, "007"
/// is 3007, "12" is 12, "012" is 3012.
/// </summary>
public static class CarNumberRaw
{
    public static int From(string carNumber)
    {
        if (string.IsNullOrEmpty(carNumber) || carNumber.Length > 3 || !carNumber.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A car number is 1 to 3 digits.", nameof(carNumber));
        }

        var number = int.Parse(carNumber, CultureInfo.InvariantCulture);
        return carNumber.Length > 1 && carNumber[0] == '0' ? number + 1000 * carNumber.Length : number;
    }
}
