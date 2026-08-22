using ElosWin.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ElosWin.Views;

public sealed partial class ChatPage : Page
{
    public ChatViewModel ViewModel => MainWindow.SharedChatVm;

    public ChatPage()
    {
        InitializeComponent();
    }

    private void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (ViewModel.SendMessageCommand.CanExecute(null))
                ViewModel.SendMessageCommand.Execute(null);

            e.Handled = true;
        }
    }
}