using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Aqorin.Phone.App.ViewModels;

namespace Aqorin.Phone.App.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
    }

    private DiagnosticsViewModel? ViewModel => DataContext as DiagnosticsViewModel;

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                vm.Notice = "Clipboard is not available.";
                return;
            }

            await clipboard.SetTextAsync(vm.AllText);
            vm.Notice = $"Copied {vm.Entries.Count} log lines to the clipboard.";
        }
        catch (Exception ex)
        {
            vm.Notice = "Copy failed: " + ex.Message;
        }
    }

    private async void OnSaveLog(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        var topLevel = TopLevel.GetTopLevel(this);
        if (vm is null || topLevel is null)
        {
            return;
        }

        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save diagnostics log",
                SuggestedFileName = $"Aqorin.Phone-{DateTime.Now:yyyyMMdd-HHmmss}.log",
                DefaultExtension = "log",
                FileTypeChoices = [new FilePickerFileType("Log file") { Patterns = ["*.log", "*.txt"] }],
            });
            if (file is null)
            {
                return;
            }

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(vm.AllText);
            vm.Notice = $"Saved {vm.Entries.Count} log lines to {file.Name}.";
        }
        catch (Exception ex)
        {
            vm.Notice = "Save failed: " + ex.Message;
        }
    }
}
