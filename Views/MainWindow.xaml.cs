using System;
using ElosWin.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace ElosWin.Views;

public sealed partial class MainWindow : Window
{
    public static CallViewModel SharedCallVm { get; } = new();
    public static ChatViewModel SharedChatVm { get; } = new();
    public static SettingsViewModel SharedSettingsVm { get; } = new();

    public CallViewModel CallVm => SharedCallVm;
    public ChatViewModel ChatVm => SharedChatVm;
    public SettingsViewModel SettingsVm => SharedSettingsVm;

    private AppWindow? _appWindow;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureCustomTitleBar();
    }

    private void ConfigureCustomTitleBar()
    {
        IntPtr hWnd = WindowNative.GetWindowHandle(this);
        WindowId wndId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(wndId);

        if (_appWindow != null && AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = _appWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(20, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(40, 255, 255, 255);

            AppTitleBar.Loaded += (s, e) => SetTitleBar(AppTitleBar);
        }
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigate(typeof(CallPage));
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            switch (tag)
            {
                case "Call":
                    ContentFrame.Navigate(typeof(CallPage));
                    break;
                case "Chat":
                    ContentFrame.Navigate(typeof(ChatPage));
                    break;
                case "Settings":
                    ContentFrame.Navigate(typeof(SettingsPage));
                    break;
            }
        }
    }
}