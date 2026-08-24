using ElosWin.Services.Audio;
using ElosWin.Services.Network;
using ElosWin.Services.Settings;
using ElosWin.Services.Update;

namespace ElosWin.Services;

public static class AppServices
{
    public static SettingsService Settings { get; } = new();
    public static IAudioService Audio { get; } = new WasapiAudioService();
    public static PeerDiscoveryService Discovery { get; } = new();
    public static ChatNetworkService Chat { get; } = new();
    public static UdpVoiceClient Voice { get; } = new();
    public static ScreenNetworkService ScreenNetwork { get; } = new();
    public static UpdateService Updater { get; } = new();
}