using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Diagnostics;

namespace Aqorin.Phone.App.ViewModels;

/// <summary>Expandable technical log. Every entry has already been passed through the credential redactor.</summary>
public sealed partial class DiagnosticsViewModel : ViewModelBase
{
    private const int MaxVisibleEntries = 500;
    private readonly DiagnosticsLog _log;
    private readonly IAudioDeviceService _audio;
    private readonly IUiDispatcher _dispatcher;

    public DiagnosticsViewModel(DiagnosticsLog log, IAudioDeviceService audio, IUiDispatcher dispatcher)
    {
        _log = log;
        _audio = audio;
        _dispatcher = dispatcher;
        foreach (var entry in _log.Snapshot().TakeLast(MaxVisibleEntries))
        {
            Entries.Add(entry.ToString());
        }

        _log.EntryAdded += entry => _dispatcher.Post(() => Append(entry));
        AudioBackendText = audio.Backend.IsAvailable
            ? $"Audio: {audio.Backend.Name} {audio.Backend.Version} — {audio.GetInputDevices().Count} input / {audio.GetOutputDevices().Count} output device(s)"
            : $"Audio unavailable: {audio.Backend.UnavailableReason}";
    }

    public ObservableCollection<string> Entries { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial string AudioBackendText { get; set; }

    /// <summary>Short feedback after copy/save actions.</summary>
    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    public string AllText => string.Join(Environment.NewLine, Entries);

    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        Entries.Clear();
    }

    [RelayCommand]
    private void RefreshAudioDevices()
    {
        var inputs = _audio.GetInputDevices();
        var outputs = _audio.GetOutputDevices();
        AudioBackendText = _audio.Backend.IsAvailable
            ? $"Audio: {_audio.Backend.Name} {_audio.Backend.Version} — {inputs.Count} input / {outputs.Count} output device(s)"
            : $"Audio unavailable: {_audio.Backend.UnavailableReason}";
        foreach (var d in inputs)
        {
            Append(new DiagnosticsEntry(DateTimeOffset.Now, Microsoft.Extensions.Logging.LogLevel.Information, "Audio", $"Input : {d.Name}{(d.IsDefault ? " (default)" : string.Empty)} @ {d.DefaultSampleRate:0} Hz"));
        }

        foreach (var d in outputs)
        {
            Append(new DiagnosticsEntry(DateTimeOffset.Now, Microsoft.Extensions.Logging.LogLevel.Information, "Audio", $"Output: {d.Name}{(d.IsDefault ? " (default)" : string.Empty)} @ {d.DefaultSampleRate:0} Hz"));
        }
    }

    private void Append(DiagnosticsEntry entry)
    {
        Entries.Add(entry.ToString());
        while (Entries.Count > MaxVisibleEntries)
        {
            Entries.RemoveAt(0);
        }
    }
}
