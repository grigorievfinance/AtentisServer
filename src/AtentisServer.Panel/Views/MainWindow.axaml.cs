using Avalonia.Controls;
using Avalonia.Threading;
using AtentisServer.Panel.ViewModels;

namespace AtentisServer.Panel.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _polling;

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await PollAsync();
        Opened += async (_, _) => { await PollAsync(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Обновление состояния без наложения запросов друг на друга; сбой опроса не должен ронять окно.</summary>
    private async Task PollAsync()
    {
        if (_polling || DataContext is not MainViewModel vm) return;
        _polling = true;
        try { await vm.RefreshAsync(); }
        catch (Exception) { /* следующий опрос повторит попытку */ }
        finally { _polling = false; }
    }
}
