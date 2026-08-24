using System;
using ElosWin.Models;
using ElosWin.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace ElosWin.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel => MainWindow.SharedSettingsVm;

    public SettingsPage()
    {
        InitializeComponent();

        ViewModel.OnUpdateAvailable += ShowUpdateAvailableDialog;
        ViewModel.OnNoUpdateFound += ShowNoUpdateDialog;
        ViewModel.OnUpdateCheckFailed += ShowUpdateFailedDialog;
    }

    private async void ShowUpdateAvailableDialog(UpdateInfo info)
    {
        var dialog = new ContentDialog
        {
            Title = "Nova atualização disponível!",
            Content = $"Uma nova versão ({info.LatestVersion}) do Elos está disponível para instalação.\n\nNotas da versão:\n{info.ReleaseNotes}",
            PrimaryButtonText = "Atualizar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.StartUpdateInstallationAsync(info.DownloadUrl);
        }
    }

    private async void ShowNoUpdateDialog()
    {
        var dialog = new ContentDialog
        {
            Title = "Nenhuma atualização encontrada.",
            Content = "Você já está utilizando a versão mais recente do Elos.",
            CloseButtonText = "Fechar",
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();
    }

    private async void ShowUpdateFailedDialog()
    {
        var dialog = new ContentDialog
        {
            Title = "Não foi possível checar por atualizações.",
            Content = "Verifique sua conexão com a internet e tente novamente mais tarde.",
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();
    }
}