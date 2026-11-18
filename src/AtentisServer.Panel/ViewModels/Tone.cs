namespace AtentisServer.Panel.ViewModels;

/// <summary>Цвет индикатора состояния.</summary>
public enum Tone { Muted, Ok, Warn, Error }

public enum BannerKind { None, Info, Success, Warning, Error }

public sealed record SslOption(string Value, string Title)
{
    public override string ToString() => Title;
}
