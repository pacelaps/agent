using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.App.Themes;

/// <summary>A brush from the theme by its key.</summary>
internal static class Theme
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>The status card's strip (the WPF plan, W3).</summary>
public sealed class StripBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusStrip.Green => Theme.Brush("Green"),
        StatusStrip.Amber => Theme.Brush("Yellow"),
        StatusStrip.Blue => Theme.Brush("Brand"),
        StatusStrip.Red => Theme.Brush("Red"),
        StatusStrip.Hazard => Theme.Brush("Hazard"),
        _ => Theme.Brush("Grey"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A check's dot (parameter "text": its text colour, for values that are wrong).</summary>
public sealed class CheckBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = parameter as string == "text";
        return value switch
        {
            StatusCheck.Ok => text ? Theme.Brush("Fg") : Theme.Brush("Green"),
            StatusCheck.Warning => text ? Theme.Brush("YellowText") : Theme.Brush("Yellow"),
            StatusCheck.Problem => text ? Theme.Brush("RedText") : Theme.Brush("Red"),
            _ => text ? Theme.Brush("Fg") : Theme.Brush("GreyDot"),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A card's border: red when the check is a problem.</summary>
public sealed class CheckBorder : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is StatusCheck.Problem ? Theme.Brush("RedBorder") : Theme.Brush("Line");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Headlines in capitals (WPF has no text-transform).</summary>
public sealed class Upper : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string)?.ToUpper(CultureInfo.CurrentCulture) ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is there (not null, not empty, not None, true); "invert" turns it round.</summary>
public sealed class Shown : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var shown = value switch
        {
            null => false,
            string s => s.Length > 0,
            bool b => b,
            StatusAction a => a != StatusAction.None,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        return shown != (parameter as string == "invert") ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The status card's look while something is wrong (parameter "card" or "border").</summary>
public sealed class ProblemCard : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var problem = value is StatusStrip.Red or StatusStrip.Hazard;
        return parameter as string == "border"
            ? problem ? Theme.Brush("RedBorder") : Theme.Brush("Line")
            : problem ? Theme.Brush("RedCard") : Theme.Brush("Surface");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The eyebrow's dot and halo by the strip.</summary>
public sealed class StripDot : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var halo = parameter as string == "halo";
        return value switch
        {
            StatusStrip.Green => halo ? Theme.Brush("GreenSoft") : Theme.Brush("Green"),
            StatusStrip.Amber => halo ? Theme.Brush("YellowSoft") : Theme.Brush("Yellow"),
            StatusStrip.Blue => halo ? Theme.Brush("BrandSoft") : Theme.Brush("Brand"),
            StatusStrip.Red or StatusStrip.Hazard => halo ? Theme.Brush("RedSoft") : Theme.Brush("Red"),
            _ => halo ? Theme.Brush("SurfaceRaised") : Theme.Brush("GreyDot"),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the value's name is the parameter (the rail's checked item).</summary>
public sealed class Is : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString() == parameter as string;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
