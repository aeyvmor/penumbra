using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Penumbra.App.Services;
using Penumbra.App.ViewModels;
using Penumbra.App.Views;

namespace Penumbra.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _serviceProvider = new ServiceCollection()
            .AddPenumbraApp()
            .BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>(),
            };

            desktop.Exit += (_, _) => _serviceProvider.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
