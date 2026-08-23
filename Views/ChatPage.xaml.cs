using ElosWin.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.Specialized;

namespace ElosWin.Views;

public sealed partial class ChatPage : Page
{
    public ChatViewModel ViewModel => MainWindow.SharedChatVm;

    public ChatPage()
    {
        InitializeComponent();

        Loaded += (s, e) =>
        {
            ViewModel.IsChatPageActive = true;
            ViewModel.ChatMessages.CollectionChanged += ChatMessages_CollectionChanged;
            ScrollToBottom();
        };

        Unloaded += (s, e) =>
        {
            ViewModel.IsChatPageActive = false;
            ViewModel.ChatMessages.CollectionChanged -= ChatMessages_CollectionChanged;
        };
    }

    private void ChatMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ScrollToBottom();
    }

    private void ScrollToBottom()
    {
        if (ViewModel.ChatMessages.Count > 0)
        {
            var lastItem = ViewModel.ChatMessages[^1];
            MessagesListView.ScrollIntoView(lastItem);
        }
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