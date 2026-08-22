using ElosWin.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace ElosWin.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel => MainWindow.SharedSettingsVm;

    public SettingsPage()
    {
        InitializeComponent();
    }
}