using ElosWin.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ElosWin.Services.Update;

public class UpdateService
{
    private const string UpdateCheckUrl = "https://api.github.com/repos/luisgbr1el/ElosWin/releases/latest";
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public string CurrentVersion { get; }

    public UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Elos-Desktop-App");

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        CurrentVersion = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.1";
    }

    public async Task<UpdateInfo> CheckForUpdatesAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<GitHubReleaseResponse>(UpdateCheckUrl, _jsonOptions);
            if (response == null || string.IsNullOrWhiteSpace(response.TagName))
                return new UpdateInfo { IsUpdateAvailable = false };

            string remoteTag = response.TagName.TrimStart('v', 'V').Trim();

            if (TryParseVersion(remoteTag, out var remoteVer) && TryParseVersion(CurrentVersion, out var localVer))
            {
                if (remoteVer > localVer)
                {
                    string downloadUrl = string.Empty;
                    if (response.Assets != null && response.Assets.Length > 0)
                    {
                        var preferredAsset = Array.Find(response.Assets, a => a.BrowserDownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                             ?? response.Assets[0];

                        downloadUrl = preferredAsset.BrowserDownloadUrl;
                    }

                    return new UpdateInfo
                    {
                        IsUpdateAvailable = true,
                        LatestVersion = remoteTag,
                        DownloadUrl = downloadUrl,
                        ReleaseNotes = response.Body ?? "Melhorias de estabilidade e correções de erros."
                    };
                }
            }

            return new UpdateInfo { IsUpdateAvailable = false };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] Erro ao verificar atualizações: {ex.Message}");
            throw new InvalidOperationException("Falha na conexão ao verificar atualizações.", ex);
        }
    }

    private static bool TryParseVersion(string versionStr, out Version version)
    {
        if (Version.TryParse(versionStr, out var parsed))
        {
            version = parsed;
            return true;
        }

        var parts = versionStr.Split('.');
        if (parts.Length == 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor))
        {
            version = new Version(major, minor, 0);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    public async Task DownloadAndInstallUpdateAsync(string downloadUrl, IProgress<double>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl)) return;

        string tempInstallerPath = Path.Combine(Path.GetTempPath(), "ElosSetup_Update.exe");

        using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(tempInstallerPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                    progress?.Report((double)totalRead / totalBytes.Value * 100.0);
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = tempInstallerPath,
            UseShellExecute = true
        };

        Process.Start(startInfo);
        Environment.Exit(0);
    }

    private class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; set; }
    }

    private class GitHubAsset
    {
        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}