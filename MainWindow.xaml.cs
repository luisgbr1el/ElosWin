using ElosWin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ElosWin;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel();
        this.InitializeComponent();

        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item)
        {
            string tag = item.Tag?.ToString() ?? "";

            if (tag == "Call")
            {
                ViewModel.SelectedNavIndex = 0;
                SettingsNavItem.IsSelected = false;
            }
            else if (tag == "Settings")
            {
                ViewModel.SelectedNavIndex = 1;
                CallNavItem.IsSelected = false;
            }
        }
    }
}