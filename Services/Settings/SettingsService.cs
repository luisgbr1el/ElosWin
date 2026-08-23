using System;
using System.IO;
using System.Text.Json;

namespace ElosWin.Services.Settings;

public class AppSettings
{
    public string Username { get; set; } = Environment.MachineName;
    public string LocalPort { get; set; } = "5000";
    public string RemoteIp { get; set; } = "127.0.0.1";
    public string RemotePort { get; set; } = "5000";
    public string? SelectedMicrophoneId { get; set; }
    public string? SelectedOutputDeviceId { get; set; }

    // Áudio
    public double InputVolume { get; set; } = 100.0;
    public double OutputVolume { get; set; } = 100.0;
    public bool EnableNoiseSuppression { get; set; } = true;
    public double NoiseSuppressionLevel { get; set; } = 50.0;
    public double GateSensitivity { get; set; } = 40.0;

    // Notificações
    public bool EnableNotifications { get; set; } = true;
}

public class SettingsService
{
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Elos"
    );

    private static readonly string FilePath = Path.Combine(FolderPath, "settings.json");

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Erro ao carregar settings: {ex.Message}");
        }

        return new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            if (!Directory.Exists(FolderPath))
            {
                Directory.CreateDirectory(FolderPath);
            }

            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Erro ao salvar settings: {ex.Message}");
        }
    }
}