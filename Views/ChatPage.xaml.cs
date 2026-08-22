using ElosWin.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace ElosWin.Views;

public sealed partial class ChatPage : Page
{
    public ChatViewModel ViewModel => MainWindow.SharedChatVm;

    public ChatPage()
    {
        InitializeComponent();
    }
}