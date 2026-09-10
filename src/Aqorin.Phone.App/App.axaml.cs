using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Aqorin.Phone.App.ViewModels;
using Aqorin.Phone.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private bool _shutdownCompleted;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = AppServices.Build();
            var logger = _services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Aqorin.Phone starting on {OS} ({Arch}).", Environment.OSVersion, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

            var mainViewModel = _services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = mainViewModel };
            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.Exit += (_, _) => Shutdown();
            _ = mainViewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // Hang up, de-register and release audio before the process exits.
        Shutdown();
    }

    private void Shutdown()
    {
        if (_shutdownCompleted || _services is null)
        {
            return;
        }

        _shutdownCompleted = true;
        var services = _services;
        try
        {
            var task = Task.Run(async () => await AppServices.ShutdownAsync(services).ConfigureAwait(false));
            if (!task.Wait(TimeSpan.FromSeconds(5)))
            {
                services.GetRequiredService<ILogger<App>>().LogWarning("Shutdown timed out; exiting anyway.");
            }
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<App>>().LogError(ex, "Error during shutdown.");
        }
    }
}
