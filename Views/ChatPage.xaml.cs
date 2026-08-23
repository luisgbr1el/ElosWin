using ElosWin.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ElosWin.Views;

public sealed partial class ChatPage : Page
{
    public ChatViewModel ViewModel => MainWindow.SharedChatVm;

    public ChatPage()
    {
        InitializeComponent();

        Loaded += (s, e) => ViewModel.IsChatPageActive = true;
        Unloaded += (s, e) => ViewModel.IsChatPageActive = false;
    }

    private void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (ViewModel.SendMessageCommand.CanExecute(null))
                ViewModel.SendMessageCommand.Execute(null);

            e.Handled = true;
        }
    }
}