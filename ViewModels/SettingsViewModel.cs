using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElosWin.Services.Audio;
using ElosWin.Services.Settings;
using ElosWin.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NAudio.CoreAudioApi;

namespace ElosWin.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly IAudioService _audioService;
    private readonly DispatcherQueue _dispatcherQueue;

    [ObservableProperty]
    public partial string Username { get; set; } = Environment.MachineName;

    [ObservableProperty]
    public partial string LocalPort { get; set; } = "5000";

    [ObservableProperty]
    public partial double InputVolume { get; set; } = 100.0;

    [ObservableProperty]
    public partial double OutputVolume { get; set; } = 100.0;

    [ObservableProperty]
    public partial bool EnableNoiseSuppression { get; set; } = true;

    [ObservableProperty]
    public partial double NoiseSuppressionLevel { get; set; } = 75.0;

    [ObservableProperty]
    public partial double GateSensitivity { get; set; } = 40.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceFillGridLength))]
    [NotifyPropertyChangedFor(nameof(VoiceEmptyGridLength))]
    public partial double VoiceLevel { get; set; } = 0.0;

    public GridLength VoiceFillGridLength => new(Math.Max(0.001, VoiceLevel), GridUnitType.Star);
    public GridLength VoiceEmptyGridLength => new(Math.Max(0.001, 100.0 - VoiceLevel), GridUnitType.Star);

    [ObservableProperty]
    public partial bool IsTestingMic { get; set; } = false;

    [ObservableProperty]
    public partial string TestMicButtonText { get; set; } = "Testar microfone";

    public bool IsNotInCall => !MainWindow.SharedCallVm.IsInCall;

    public ObservableCollection<MMDevice> AvailableMicrophones { get; } = new();
    public ObservableCollection<MMDevice> AvailableOutputDevices { get; } = new();

    [ObservableProperty]
    public partial MMDevice? SelectedMicrophone { get; set; }

    [ObservableProperty]
    public partial MMDevice? SelectedOutputDevice { get; set; }

    public SettingsViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _settingsService = new SettingsService();
        _audioService = MainWindow.SharedCallVm.AudioService;

        var saved = _settingsService.LoadSettings();
        Username = saved.Username;
        LocalPort = saved.LocalPort;
        InputVolume = saved.InputVolume;
        OutputVolume = saved.OutputVolume;
        EnableNoiseSuppression = saved.EnableNoiseSuppression;
        NoiseSuppressionLevel = saved.NoiseSuppressionLevel;
        GateSensitivity = saved.GateSensitivity;

        _audioService.OnVoiceLevelChanged += (level) =>
        {
            _dispatcherQueue.TryEnqueue(() => VoiceLevel = Math.Clamp(level * 100.0, 0.0, 100.0));
        };

        LoadDevices(saved.SelectedMicrophoneId, saved.SelectedOutputDeviceId);
    }

    private void LoadDevices(string? savedInId, string? savedOutId)
    {
        AvailableMicrophones.Clear();
        foreach (var dev in _audioService.GetInputDevices()) AvailableMicrophones.Add(dev);
        SelectedMicrophone = AvailableMicrophones.FirstOrDefault(d => d.ID == savedInId) ?? AvailableMicrophones.FirstOrDefault();

        AvailableOutputDevices.Clear();
        foreach (var dev in _audioService.GetOutputDevices()) AvailableOutputDevices.Add(dev);
        SelectedOutputDevice = AvailableOutputDevices.FirstOrDefault(d => d.ID == savedOutId) ?? AvailableOutputDevices.FirstOrDefault();
    }

    private void PersistSettings()
    {
        _settingsService.SaveSettings(new AppSettings
        {
            Username = Username,
            LocalPort = LocalPort,
            SelectedMicrophoneId = SelectedMicrophone?.ID,
            SelectedOutputDeviceId = SelectedOutputDevice?.ID,
            InputVolume = InputVolume,
            OutputVolume = OutputVolume,
            EnableNoiseSuppression = EnableNoiseSuppression,
            NoiseSuppressionLevel = NoiseSuppressionLevel,
            GateSensitivity = GateSensitivity
        });
    }

    partial void OnUsernameChanged(string value) => PersistSettings();
    partial void OnLocalPortChanged(string value) => PersistSettings();
    partial void OnInputVolumeChanged(double value) { _audioService.InputVolumeMultiplier = (float)(value / 100.0); PersistSettings(); }
    partial void OnOutputVolumeChanged(double value) { _audioService.OutputVolumeMultiplier = (float)(value / 100.0); PersistSettings(); }
    partial void OnEnableNoiseSuppressionChanged(bool value) { _audioService.EnableNoiseSuppression = value; PersistSettings(); }
    partial void OnNoiseSuppressionLevelChanged(double value) { _audioService.NoiseSuppressionStrength = (float)(value / 100.0); PersistSettings(); }
    partial void OnGateSensitivityChanged(double value) { _audioService.GateSensitivity = (float)(value / 100.0); PersistSettings(); }

    [RelayCommand]
    private void ToggleTestMic()
    {
        if (!IsTestingMic)
        {
            _audioService.StartLoopback(SelectedMicrophone?.ID, SelectedOutputDevice?.ID);
            IsTestingMic = true;
            TestMicButtonText = "Parar teste";
        }
        else
        {
            _audioService.Stop();
            IsTestingMic = false;
            TestMicButtonText = "Testar microfone";
            VoiceLevel = 0;
        }
    }
}