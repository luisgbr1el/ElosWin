using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElosWin.Models;
using ElosWin.Services;
using ElosWin.Services.Network;
using Microsoft.UI.Dispatching;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ElosWin.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

    private const string AppUserModelId = "Elos.VoiceChat.Desktop";
    private readonly DispatcherQueue _dispatcherQueue;

    public ObservableCollection<ChatMessage> ChatMessages { get; } = new();

    public ChatNetworkService ChatService { get; }

    [ObservableProperty]
    public partial string ChatInputMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ChatStatusText { get; set; } = "0 mensagens";

    public bool HasChatMessages => ChatMessages.Count > 0;
    public bool NoChatMessages => ChatMessages.Count == 0;

    public ChatViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        ChatService = AppServices.Chat;

        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch { }

        var savedSettings = AppServices.Settings.LoadSettings();

        if (int.TryParse(savedSettings.LocalPort, out int port))
        {
            ChatService.Start(port);
        }

        ChatService.OnMessageReceived += (message, senderEp) =>
        {
            var currentSettings = AppServices.Settings.LoadSettings();
            if (message.SenderName.Equals(currentSettings.Username, StringComparison.OrdinalIgnoreCase))
                return;

            _dispatcherQueue.TryEnqueue(() =>
            {
                message.IsFromSelf = false;
                ChatMessages.Add(message);

                OnPropertyChanged(nameof(HasChatMessages));
                OnPropertyChanged(nameof(NoChatMessages));
                ChatStatusText = $"{ChatMessages.Count} {(ChatMessages.Count == 1 ? "mensagem" : "mensagens")}";

                if (currentSettings.EnableNotifications)
                {
                    ShowNativeToastNotification(message.SenderName, message.Content);
                }
            });
        };
    }

    private void ShowNativeToastNotification(string title, string content)
    {
        try
        {
            string safeTitle = System.Security.SecurityElement.Escape(title);
            string safeContent = System.Security.SecurityElement.Escape(content);

            string toastXmlString = $@"
            <toast>
                <visual>
                    <binding template='ToastGeneric'>
                        <text>{safeTitle}</text>
                        <text>{safeContent}</text>
                    </binding>
                </visual>
            </toast>";

            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(toastXmlString);

            var toast = new ToastNotification(xmlDoc);
            ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(toast);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Erro ao exibir toast: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(ChatInputMessage)) return;

        var savedSettings = AppServices.Settings.LoadSettings();

        var message = new ChatMessage
        {
            SenderName = savedSettings.Username,
            Content = ChatInputMessage.Trim(),
            Timestamp = DateTime.Now,
            IsFromSelf = true
        };

        ChatMessages.Add(message);
        ChatInputMessage = string.Empty;

        OnPropertyChanged(nameof(HasChatMessages));
        OnPropertyChanged(nameof(NoChatMessages));
        ChatStatusText = $"{ChatMessages.Count} {(ChatMessages.Count == 1 ? "mensagem" : "mensagens")}";

        await ChatService.BroadcastMessageAsync(message);
    }
}