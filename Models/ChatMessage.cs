using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ElosWin.Models;

public partial class ChatMessage : ObservableObject
{
    [ObservableProperty]
    public partial string Id { get; set; } = Guid.NewGuid().ToString();

    [ObservableProperty]
    public partial string SenderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Content { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTime Timestamp { get; set; } = DateTime.Now;

    [ObservableProperty]
    public partial bool IsFromSelf { get; set; } = false;

    [JsonIgnore]
    public string TimestampText => Timestamp.ToString("HH:mm");

    [JsonIgnore]
    public HorizontalAlignment Alignment => IsFromSelf ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    [JsonIgnore]
    public SolidColorBrush BubbleBackgroundBrush => IsFromSelf
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(50, 0, 120, 212))
        : new SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128));

    public static Visibility GetAvatarVisibility(bool isFromSelf) => isFromSelf ? Visibility.Collapsed : Visibility.Visible;
}