using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace KaraokeMixer.App;

/// <summary>A microphone choice for the device-picker ComboBox. <see cref="DeviceId"/> is null for
/// the "System default" pseudo-entry — <see cref="AudioMixerCore.StartAsync"/> already treats a
/// null device id as "use the default recording device", so no separate lookup is needed here.
/// ToString() drives the ComboBox's default display (no DataTemplate needed).</summary>
public sealed record MicDeviceOption(string DisplayName, string? DeviceId)
{
    public override string ToString() => DisplayName;
}

/// <summary>Same shape as <see cref="MicDeviceOption"/>, kept as a distinct type for the output
/// (speaker/headphone) picker so XAML bindings and code-behind read unambiguously about which
/// picker a value came from.</summary>
public sealed record OutputDeviceOption(string DisplayName, string? DeviceId)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// ViewModel for MainWindow. Holds display/diagnostic state for both the WebView2 sign-in flow
/// (carried over from WebViewLoginSpike) and the karaoke mixer control panel (new). WebView2 and
/// AudioMixerCore logic itself stays in code-behind (same rationale as the spike: it needs direct
/// access to CoreWebView2 and isn't naturally MVVM-testable anyway), this class is purely bindable
/// state.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    // --- WebView2 / sign-in state (from WebViewLoginSpike) ---------------------------------

    [ObservableProperty]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    public partial bool CanGoForward { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Warning { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity WarningSeverity { get; set; } = InfoBarSeverity.Informational;

    // --- Mixer engine state ------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsEngineRunning { get; set; }

    [ObservableProperty]
    public partial bool IsEngineStarting { get; set; }

    [ObservableProperty]
    public partial string EngineStatus { get; set; } = "Đã dừng";

    [ObservableProperty]
    public partial ObservableCollection<MicDeviceOption> MicDevices { get; set; } = [];

    [ObservableProperty]
    public partial MicDeviceOption? SelectedMicDevice { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<OutputDeviceOption> OutputDevices { get; set; } = [];

    [ObservableProperty]
    public partial OutputDeviceOption? SelectedOutputDevice { get; set; }

    [ObservableProperty]
    public partial double MicVolumePercent { get; set; } = 100;

    [ObservableProperty]
    public partial double MusicVolumePercent { get; set; } = 85;

    [ObservableProperty]
    public partial double EqLowDb { get; set; }

    [ObservableProperty]
    public partial double EqMidDb { get; set; }

    [ObservableProperty]
    public partial double EqHighDb { get; set; }

    [ObservableProperty]
    public partial bool EchoEnabled { get; set; }

    [ObservableProperty]
    public partial double EchoDelayMs { get; set; } = 200;

    [ObservableProperty]
    public partial double EchoFeedbackPercent { get; set; } = 35;

    [ObservableProperty]
    public partial double EchoMixPercent { get; set; } = 30;

    [ObservableProperty]
    public partial double MicPeakPercent { get; set; }

    [ObservableProperty]
    public partial double MasterPeakPercent { get; set; }
}
