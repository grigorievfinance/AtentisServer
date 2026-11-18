using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AtentisServer.Contracts;
using AtentisServer.Panel.ViewModels;
using AtentisServer.Panel.Views;

namespace AtentisServer.Panel;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(new ControlClient()) };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
