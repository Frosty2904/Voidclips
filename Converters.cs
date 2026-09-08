using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using VoidClip.Models;
using VoidClip.Services;

namespace VoidClip;

public class EmptyToCollapsed : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => string.IsNullOrWhiteSpace(v as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class BoolToVisibility : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        var b = v is bool bb && bb;
        if (p as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class PrettyHotkey : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => HotkeyService.Pretty(v as string);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Hex string to brush, with a safe fallback.</summary>
public class HexToBrush : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        var hex = v as string;
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch { }
        return new SolidColorBrush(Color.FromRgb(0x8B, 0x3D, 0xFF));
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>A clip's accent: its own colour override, else its category's, else purple.</summary>
public class ClipAccent : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        var clip = v as Clip;
        var hex = clip?.Color;
        if (string.IsNullOrWhiteSpace(hex))
            hex = Core.Library.CategoryById(clip?.CategoryId)?.Color;
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
            {
                var col = (Color)ColorConverter.ConvertFromString(hex);
                if (p as string == "color") return col;
                return new SolidColorBrush(col);
            }
        }
        catch { }
        var fallback = Color.FromRgb(0x8B, 0x3D, 0xFF);
        return p as string == "color" ? fallback : new SolidColorBrush(fallback);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class CategoryName : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => Core.Library.CategoryById(v as string)?.Name ?? "Uncategorised";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
