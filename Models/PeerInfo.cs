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
    private string _username = string.Empty;

    [ObservableProperty]
    private string _ipAddress = string.Empty;

    [ObservableProperty]
    private int _audioPort;

    [ObservableProperty]
    private DateTime _lastSeen;

    [ObservableProperty]
    private DateTime _lastSpokeTime = DateTime.MinValue;

    [ObservableProperty]
    private bool _isLocalUser = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeakingBorderBrush))]
    private bool _isSpeaking = false;

    public SolidColorBrush SpeakingBorderBrush => IsSpeaking ? ActiveVoiceBrush : InactiveVoiceBrush;

    public string DisplayName => IsLocalUser ? $"{Username} (Você)" : Username;
    public string Subtitle => IsLocalUser ? "Dispositivo local" : $"{IpAddress}:{AudioPort}";

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