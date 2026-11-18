using Avalonia.Data.Converters;
using Avalonia.Media;
using AtentisServer.Panel.ViewModels;

namespace AtentisServer.Panel.Converters;

/// <summary>Цвета индикаторов состояния и сообщений.</summary>
public static class Tones
{
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static readonly FuncValueConverter<Tone, IBrush> PillBackground = new(t => t switch
    {
        Tone.Ok => B("#E7F6EC"), Tone.Warn => B("#FFF4D6"), Tone.Error => B("#FDE8E8"), _ => B("#EEF1F6")
    });

    public static readonly FuncValueConverter<Tone, IBrush> PillForeground = new(t => t switch
    {
        Tone.Ok => B("#166534"), Tone.Warn => B("#92400E"), Tone.Error => B("#B42318"), _ => B("#475467")
    });

    public static readonly FuncValueConverter<Tone, IBrush> Dot = new(t => t switch
    {
        Tone.Ok => B("#22A455"), Tone.Warn => B("#F59E0B"), Tone.Error => B("#E5484D"), _ => B("#98A2B3")
    });

    public static readonly FuncValueConverter<BannerKind, IBrush> BannerBackground = new(k => k switch
    {
        BannerKind.Success => B("#E7F6EC"), BannerKind.Warning => B("#FFF4D6"), BannerKind.Error => B("#FDE8E8"), _ => B("#E8F0FE")
    });

    public static readonly FuncValueConverter<BannerKind, IBrush> BannerBorder = new(k => k switch
    {
        BannerKind.Success => B("#9AD5B0"), BannerKind.Warning => B("#F5D77F"), BannerKind.Error => B("#F4A9A8"), _ => B("#A9C4F5")
    });

    public static readonly FuncValueConverter<BannerKind, IBrush> BannerForeground = new(k => k switch
    {
        BannerKind.Success => B("#14532D"), BannerKind.Warning => B("#7A4A05"), BannerKind.Error => B("#912018"), _ => B("#1E3A8A")
    });
}
