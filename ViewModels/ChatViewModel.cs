using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElosWin.Models;
using ElosWin.Views;

namespace ElosWin.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    public ObservableCollection<ChatMessage> ChatMessages { get; } = new();

    [ObservableProperty]
    public partial string ChatInputMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ChatStatusText { get; set; } = "0 mensagens";

    public bool HasChatMessages => ChatMessages.Count > 0;
    public bool NoChatMessages => ChatMessages.Count == 0;

    [RelayCommand]
    public void SendMessage()
    {
        if (string.IsNullOrWhiteSpace(ChatInputMessage)) return;

        var message = new ChatMessage
        {
            SenderName = MainWindow.SharedSettingsVm.Username,
            Content = ChatInputMessage.Trim(),
            Timestamp = DateTime.Now,
            IsFromSelf = true
        };

        ChatMessages.Add(message);
        ChatInputMessage = string.Empty;

        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(NoChatMessages));
        ChatStatusText = $"{ChatMessages.Count} {(ChatMessages.Count == 1 ? "mensagem" : "mensagens")}";
    }
}