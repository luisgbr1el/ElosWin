using ElosWin.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using WinRT.Interop;

namespace ElosWin.Views;

public sealed partial class MainWindow : Window
{
    private static readonly Lazy<CallViewModel> _sharedCallVm = new(() => new CallViewModel());
    private static readonly Lazy<SettingsViewModel> _sharedSettingsVm = new(() => new SettingsViewModel());
    private static readonly Lazy<ChatViewModel> _sharedChatVm = new(() => new ChatViewModel());

    public static CallViewModel SharedCallVm => _sharedCallVm.Value;
    public static SettingsViewModel SharedSettingsVm => _sharedSettingsVm.Value;
    public static ChatViewModel SharedChatVm => _sharedChatVm.Value;

    public CallViewModel CallVm => SharedCallVm;
    public ChatViewModel ChatVm => SharedChatVm;
    public SettingsViewModel SettingsVm => SharedSettingsVm;

    private AppWindow? _appWindow;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureCustomTitleBar();

        _ = SharedSettingsVm;
        _ = SharedCallVm;
        _ = SharedChatVm;

        CheckForStartupUpdates();
    }

    private void CheckForStartupUpdates()
    {
        Task.Run(async () =>
        {
            await Task.Delay(2000);

            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var updateService = Services.AppServices.Updater;
                    var updateInfo = await updateService.CheckForUpdatesAsync();

                    if (updateInfo.IsUpdateAvailable && Content?.XamlRoot != null)
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "Nova atualização disponível!",
                            Content = $"Uma nova versão ({updateInfo.LatestVersion}) do Elos está disponível para instalação.\n\nNotas da versão:\n{updateInfo.ReleaseNotes}",
                            PrimaryButtonText = "Atualizar",
                            CloseButtonText = "Cancelar",
                            DefaultButton = ContentDialogButton.Primary,
                            XamlRoot = Content.XamlRoot
                        };

                        var result = await dialog.ShowAsync();
                        
                        if (result == ContentDialogResult.Primary)
                            await updateService.DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);
                    }
                }
                catch
                {
                }
            });
        });
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