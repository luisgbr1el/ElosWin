using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace ElosWin.Models;

public partial class PeerInfo : ObservableObject
{
    private static readonly SolidColorBrush ActiveVoiceBrush = new(ColorHelper.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush InactiveVoiceBrush = new(ColorHelper.FromArgb(0, 0, 0, 0));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _username = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string _ipAddress = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private int _audioPort;

    [ObservableProperty]
    private DateTime _lastSeen;

    [ObservableProperty]
    private DateTime _lastSpokeTime = DateTime.MinValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(IsRemoteUser))]
    private bool _isLocalUser = false;

    public bool IsRemoteUser => !IsLocalUser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeakingBorderBrush))]
    private bool _isSpeaking = false;

    public SolidColorBrush SpeakingBorderBrush => IsSpeaking ? ActiveVoiceBrush : InactiveVoiceBrush;

    public string DisplayName => IsLocalUser ? $"{Username} (Você)" : Username;
    public string Subtitle => IsLocalUser ? "Dispositivo local" : $"{IpAddress}:{AudioPort}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserVolumeText))]
    private double _userVolume = 100.0;

    public string UserVolumeText => $"{(int)UserVolume}%";

    [ObservableProperty]
    private bool _isLocallyMuted = false;

    public override bool Equals(object? obj)
    {
        if (obj is PeerInfo other)
        {
            if (IsLocalUser && other.IsLocalUser) return true;
            return IpAddress == other.IpAddress && AudioPort == other.AudioPort;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(IpAddress, AudioPort, IsLocalUser);
    }
}