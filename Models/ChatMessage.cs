using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ElosWin.Models;

public partial class ChatMessage : ObservableObject
{
    [ObservableProperty]
    public partial string SenderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Content { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTime Timestamp { get; set; } = DateTime.Now;

    [ObservableProperty]
    public partial bool IsFromSelf { get; set; } = false;

    public string TimestampText => Timestamp.ToString("HH:mm");

    public HorizontalAlignment Alignment => IsFromSelf ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public SolidColorBrush BubbleBackgroundBrush => IsFromSelf
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(50, 0, 120, 212))
        : new SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128));
}