using System;
using ElosWin.Models;
using ElosWin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ElosWin.Views;

public sealed partial class CallPage : Page
{
    public CallViewModel ViewModel => MainWindow.SharedCallVm;

    public CallPage()
    {
        InitializeComponent();
    }

    private async void ShareScreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsSharingScreenLocal)
        {
            ViewModel.StopScreenShare();
            return;
        }

        ViewModel.PrepareCaptureTargets();

        var dialog = new ContentDialog
        {
            Title = "Compartilhar sua tela",
            PrimaryButtonText = "Transmitir ao vivo",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var stack = new StackPanel { Spacing = 16, Width = 380 };

        var comboTarget = new ComboBox
        {
            Header = "O que você quer compartilhar?",
            ItemsSource = ViewModel.AvailableCaptureTargets,
            SelectedItem = ViewModel.SelectedCaptureTarget,
            DisplayMemberPath = "Title",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        comboTarget.SelectionChanged += (s, ev) => ViewModel.SelectedCaptureTarget = comboTarget.SelectedItem as CaptureTargetItem;

        var comboQuality = new ComboBox
        {
            Header = "Qualidade da transmissão",
            ItemsSource = ViewModel.AvailableQualities,
            SelectedItem = ViewModel.SelectedQuality,
            DisplayMemberPath = "Name",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        comboQuality.SelectionChanged += (s, ev) => ViewModel.SelectedQuality = comboQuality.SelectedItem as ScreenShareQuality;

        var toggleAudio = new ToggleSwitch
        {
            Header = "Compartilhar áudio do sistema",
            IsOn = ViewModel.ShareScreenAudio,
            OnContent = "Sim",
            OffContent = "Não"
        };
        toggleAudio.Toggled += (s, ev) => ViewModel.ShareScreenAudio = toggleAudio.IsOn;

        stack.Children.Add(comboTarget);
        stack.Children.Add(comboQuality);
        stack.Children.Add(toggleAudio);
        dialog.Content = stack;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.StartScreenSharingFromDialog();
        }
    }
}