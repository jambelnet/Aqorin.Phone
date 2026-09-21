using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Aqorin.Phone.App.ViewModels;
using Aqorin.Phone.Mobile.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private MainView? _mainView;
    private bool _shutdownCompleted;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            var mainViewModel = StartServices();
            activityLifetime.MainViewFactory = () => CreateMainView(mainViewModel);
            _ = mainViewModel.InitializeAsync();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var mainViewModel = StartServices();
            singleView.MainView = CreateMainView(mainViewModel);
            _ = mainViewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal void RefreshMainViewLayout() => _mainView?.RefreshAfterForeground();

    private MainView CreateMainView(MainWindowViewModel viewModel)
    {
        _mainView = new MainView { DataContext = viewModel };
        return _mainView;
    }

    private MainWindowViewModel StartServices()
    {
        _services = AppServices.Build();
        var logger = _services.GetRequiredService<ILogger<App>>();
        logger.LogInformation(
            "Aqorin.Phone mobile starting on {OS} ({Arch}).",
            Environment.OSVersion,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

        return _services.GetRequiredService<MainWindowViewModel>();
    }

    public async Task ShutdownAsync()
    {
        if (_shutdownCompleted || _services is null)
        {
            return;
        }

        _shutdownCompleted = true;
        await AppServices.ShutdownAsync(_services).ConfigureAwait(false);
    }
}
