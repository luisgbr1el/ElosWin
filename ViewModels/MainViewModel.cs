using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Concentus;
using Concentus.Enums;
using ElosWin.Models;
using ElosWin.Models.Enums;
using ElosWin.Services.Audio;
using ElosWin.Services.Network;
using ElosWin.Services.ScreenShare;
using ElosWin.Services.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.CoreAudioApi;
using Windows.Storage.Streams;

namespace ElosWin.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int ScreenAudioSampleRate = 48000;
    private const int ScreenAudioChannels = 1;
    private const int ScreenAudioFrameSize = 960;
    private const int ScreenAudioFrameBytes = ScreenAudioFrameSize * sizeof(short);

    private readonly IAudioService _audioService;
    private readonly UdpVoiceClient _udpClient;
    private readonly PeerDiscoveryService _discoveryService;
    private readonly SettingsService _settingsService;
    private readonly ScreenCaptureService _screenCaptureService;
    private readonly ScreenNetworkService _screenNetworkService;
    private readonly DispatcherQueue _dispatcherQueue;

    private readonly IOpusEncoder _screenAudioEncoder;
    private readonly object _screenAudioLock = new();
    private readonly byte[] _screenAudioAccumulator = new byte[ScreenAudioFrameBytes * 4];
    private int _screenAudioAccumulatorOffset = 0;

    private CancellationTokenSource? _vadDecayCts;

    // Navegação
    [ObservableProperty]
    public partial int SelectedNavIndex { get; set; } = 0;

    public bool IsCallPageVisible => SelectedNavIndex == 0;
    public bool IsSettingsPageVisible => SelectedNavIndex == 1;

    // Configurações de Usuário e Rede
    [ObservableProperty]
    public partial string Username { get; set; } = Environment.MachineName;

    [ObservableProperty]
    public partial string LocalPort { get; set; } = "5000";

    // Volumes e Supressão
    [ObservableProperty]
    public partial double InputVolume { get; set; } = 100.0;

    [ObservableProperty]
    public partial double OutputVolume { get; set; } = 100.0;

    [ObservableProperty]
    public partial bool EnableNoiseSuppression { get; set; } = true;

    [ObservableProperty]
    public partial double NoiseSuppressionLevel { get; set; } = 75.0;

    [ObservableProperty]
    public partial double GateSensitivity { get; set; } = 40.0;

    // Estado da chamada
    [ObservableProperty]
    public partial string StatusText { get; set; } = "Nenhuma chamada na rede";

    [ObservableProperty]
    public partial string MainActionButtonText { get; set; } = "Iniciar chamada";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInCall))]
    public partial bool IsInCall { get; set; } = false;

    public bool IsNotInCall => !IsInCall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParticipantsInCall))]
    [NotifyPropertyChangedFor(nameof(NoParticipantsInCall))]
    public partial int ParticipantsCount { get; set; } = 0;

    public bool HasParticipantsInCall => ParticipantsCount > 0;
    public bool NoParticipantsInCall => ParticipantsCount == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteGlyph))]
    [NotifyPropertyChangedFor(nameof(MuteToolTip))]
    public partial bool IsMuted { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeafenGlyph))]
    [NotifyPropertyChangedFor(nameof(DeafenToolTip))]
    [NotifyPropertyChangedFor(nameof(CanToggleMute))]
    public partial bool IsDeafened { get; set; } = false;

    public bool CanToggleMute => !IsDeafened;
    public string MuteGlyph => IsMuted ? "\uF781" : "\uE720";
    public string MuteToolTip => IsMuted ? "Ativar microfone" : "Silenciar microfone";
    public string DeafenGlyph => IsDeafened ? "\uE74F" : "\uE767";
    public string DeafenToolTip => IsDeafened ? "Ativar som" : "Desativar som";

    // Nível de voz local
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceFillGridLength))]
    [NotifyPropertyChangedFor(nameof(VoiceEmptyGridLength))]
    public partial double VoiceLevel { get; set; } = 0.0;

    public GridLength VoiceFillGridLength => new(Math.Max(0.001, VoiceLevel), GridUnitType.Star);
    public GridLength VoiceEmptyGridLength => new(Math.Max(0.001, 100.0 - VoiceLevel), GridUnitType.Star);

    // Teste de microfone
    [ObservableProperty]
    public partial bool IsTestingMic { get; set; } = false;

    [ObservableProperty]
    public partial string TestMicButtonText { get; set; } = "Testar microfone";

    // Compartilhamento de tela
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenShareButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(ScreenShareButtonToolTip))]
    [NotifyPropertyChangedFor(nameof(ScreenShareButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(ScreenShareButtonToolTip))]
    [NotifyPropertyChangedFor(nameof(CanShowScreenShareFullscreenButton))]
    public partial bool IsSharingScreenLocal { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowScreenShareFullscreenButton))]
    public partial bool HasActiveScreenStream { get; set; } = false;

    [ObservableProperty]
    private bool _isScreenShareFullscreen;

    [ObservableProperty]
    public partial string ScreenStreamPresenterText { get; set; } = "Transmissão de tela";

    [ObservableProperty]
    public partial BitmapImage? RemoteScreenImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamVolumeText))]
    public partial double StreamVolume { get; set; } = 100.0;

    public string StreamVolumeText => $"{(int)StreamVolume}%";

    public string ScreenShareButtonGlyph => IsSharingScreenLocal ? "\uEA14" : "\uE7F4";
    public string ScreenShareButtonToolTip => IsSharingScreenLocal ? "Parar compartilhamento" : "Compartilhar tela";
    public Visibility CanShowScreenShareFullscreenButton => (HasActiveScreenStream && !IsSharingScreenLocal) ? Visibility.Visible : Visibility.Collapsed;

    // Opções de Modal/Configuração de Transmissão
    public ObservableCollection<CaptureTargetItem> AvailableCaptureTargets { get; } = new();

    [ObservableProperty]
    public partial CaptureTargetItem? SelectedCaptureTarget { get; set; }

    public ObservableCollection<ScreenShareQuality> AvailableQualities { get; } = new()
    {
        new ScreenShareQuality("720p 30 FPS", 1280, 720, 30, 65),
        new ScreenShareQuality("1080p 60 FPS", 1920, 1080, 60, 75)
    };

    [ObservableProperty]
    public partial ScreenShareQuality SelectedQuality { get; set; }

    [ObservableProperty]
    public partial bool ShareScreenAudio { get; set; } = true;

    // Dispositivos
    public ObservableCollection<MMDevice> AvailableMicrophones { get; } = new();
    public ObservableCollection<MMDevice> AvailableOutputDevices { get; } = new();

    [ObservableProperty]
    public partial MMDevice? SelectedMicrophone { get; set; }

    [ObservableProperty]
    public partial MMDevice? SelectedOutputDevice { get; set; }

    // Participantes na chamada
    public ObservableCollection<PeerInfo> ConnectedParticipants { get; } = new();

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _settingsService = new SettingsService();

        _audioService = new WasapiAudioService();
        _udpClient = new UdpVoiceClient();
        _discoveryService = new PeerDiscoveryService();
        _screenCaptureService = new ScreenCaptureService();
        _screenNetworkService = new ScreenNetworkService();

        _screenAudioEncoder = OpusCodecFactory.CreateEncoder(ScreenAudioSampleRate, ScreenAudioChannels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _screenAudioEncoder.Bitrate = 96000;

        SelectedQuality = AvailableQualities[0];

        var saved = _settingsService.LoadSettings();
        Username = saved.Username;
        LocalPort = saved.LocalPort;
        InputVolume = saved.InputVolume;
        OutputVolume = saved.OutputVolume;
        EnableNoiseSuppression = saved.EnableNoiseSuppression;
        NoiseSuppressionLevel = saved.NoiseSuppressionLevel;
        GateSensitivity = saved.GateSensitivity;

        _audioService.InputVolumeMultiplier = (float)(InputVolume / 100.0);
        _audioService.OutputVolumeMultiplier = (float)(OutputVolume / 100.0);
        _audioService.EnableNoiseSuppression = EnableNoiseSuppression;
        _audioService.NoiseSuppressionStrength = (float)(NoiseSuppressionLevel / 100.0);
        _audioService.GateSensitivity = (float)(GateSensitivity / 100.0);

        _audioService.OnVoiceLevelChanged += (level) =>
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                VoiceLevel = Math.Clamp(level * 100.0, 0.0, 100.0);

                if (IsInCall)
                {
                    var localUser = ConnectedParticipants.FirstOrDefault(p => p.IsLocalUser);
                    if (localUser != null && !IsMuted && !IsDeafened)
                    {
                        if (level > 0.035f)
                        {
                            localUser.IsSpeaking = true;
                            localUser.LastSpokeTime = DateTime.Now;
                        }
                    }
                }
            });
        };

        _udpClient.OnAudioPacketReceived += (opusPacket, senderEp) =>
        {
            if (!IsInCall) return;

            string senderIp = senderEp.Address.ToString();
            var remotePeer = ConnectedParticipants.FirstOrDefault(p => !p.IsLocalUser && (p.IpAddress == senderIp || p.AudioPort == senderEp.Port));

            if (remotePeer != null && remotePeer.IsLocallyMuted)
                return;

            string senderKey = senderEp.ToString();
            float peerVol = (remotePeer != null) ? (float)(remotePeer.UserVolume / 100.0) : 1.0f;

            float level = _audioService.PlayReceivedFrameFromSender(opusPacket, senderKey, peerVol);

            if (level > 0.035f && remotePeer != null)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    remotePeer.IsSpeaking = true;
                    remotePeer.LastSpokeTime = DateTime.Now;
                });
            }
        };

        _screenNetworkService.OnFrameReassembled += async (jpegBytes) =>
        {
            if (!IsInCall) return;

            _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    using var ms = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(ms.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(jpegBytes);
                        await writer.StoreAsync();
                    }

                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(ms);
                    RemoteScreenImage = bitmap;
                    HasActiveScreenStream = true;
                }
                catch { }
            });
        };

        _screenNetworkService.OnScreenAudioPacketReceived += (opusPacket, senderEp) =>
        {
            if (!IsInCall || IsDeafened) return;
            float streamVol = (float)(StreamVolume / 100.0);
            _audioService.PlayReceivedFrameFromSender(opusPacket, "screenshare_stream", streamVol);
        };

        _discoveryService.OnPeerJoinedCall += (peer) =>
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                var existing = ConnectedParticipants.FirstOrDefault(p => !p.IsLocalUser && p.Equals(peer));
                if (existing == null)
                {
                    ConnectedParticipants.Add(peer);
                    if (IsInCall)
                    {
                        _udpClient.AddTarget(peer.IpAddress, peer.AudioPort);
                        _screenNetworkService.AddTarget(peer.IpAddress, peer.AudioPort + 100);
                    }
                }
                ParticipantsCount = ConnectedParticipants.Count;
                UpdateCallStatusMessage();
                CheckScreenStreamPresence();
            });
        };

        _discoveryService.OnPeerLeftCall += (peer) =>
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                var existing = ConnectedParticipants.FirstOrDefault(p => !p.IsLocalUser && p.Equals(peer));
                if (existing != null)
                {
                    ConnectedParticipants.Remove(existing);
                    if (IsInCall)
                    {
                        _udpClient.RemoveTarget(peer.IpAddress, peer.AudioPort);
                        _screenNetworkService.RemoveTarget(peer.IpAddress, peer.AudioPort + 100);
                        string senderKey = $"{peer.IpAddress}:{peer.AudioPort}";
                        _audioService.RemovePeerDecoder(senderKey);
                    }
                }
                ParticipantsCount = ConnectedParticipants.Count;
                UpdateCallStatusMessage();
                CheckScreenStreamPresence();
            });
        };

        _discoveryService.OnPeerStateChanged += (peer) =>
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (peer.State == UserState.SharingScreen && !peer.IsLocalUser)
                {
                    if (IsSharingScreenLocal)
                        StopScreenSharingInternal();
                }

                CheckScreenStreamPresence();
            });
        };

        _discoveryService.OnCallStateUpdated += (peersInCallCount) =>
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                ParticipantsCount = ConnectedParticipants.Count;
                UpdateCallStatusMessage();
                CheckScreenStreamPresence();
            });
        };

        LoadDevices(saved.SelectedMicrophoneId, saved.SelectedOutputDeviceId);

        if (int.TryParse(LocalPort, out int port))
            _discoveryService.Start(Username, port);
    }

    private void CheckScreenStreamPresence()
    {
        var presenter = ConnectedParticipants.FirstOrDefault(p => p.State == UserState.SharingScreen);
        if (presenter != null)
        {
            HasActiveScreenStream = true;
            ScreenStreamPresenterText = presenter.IsLocalUser ? "Você está transmitindo" : $"{presenter.Username} está transmitindo";
        }
        else
        {
            HasActiveScreenStream = false;
            RemoteScreenImage = null;
        }
    }

    public void PrepareCaptureTargets()
    {
        AvailableCaptureTargets.Clear();
        var targets = ScreenCaptureService.GetAvailableCaptureTargets();
        foreach (var t in targets) AvailableCaptureTargets.Add(t);
        SelectedCaptureTarget = AvailableCaptureTargets.FirstOrDefault();
    }

    public void StartScreenSharingFromDialog()
    {
        if (!IsInCall || SelectedCaptureTarget == null) return;

        lock (_screenAudioLock)
        {
            _screenAudioAccumulatorOffset = 0;
        }

        IsSharingScreenLocal = true;
        _discoveryService.CurrentState = UserState.SharingScreen;
        _discoveryService.BroadcastImmediateState();

        var localUser = ConnectedParticipants.FirstOrDefault(p => p.IsLocalUser);
        if (localUser != null) localUser.State = UserState.SharingScreen;

        CheckScreenStreamPresence();

        _screenCaptureService.StartCapture(
            SelectedCaptureTarget,
            SelectedQuality,
            ShareScreenAudio,
            (frameData) =>
            {
                if (IsSharingScreenLocal)
                    _screenNetworkService.BroadcastVideoFrame(frameData);
            },
            (audioData, bytesRecorded) =>
            {
                if (!IsSharingScreenLocal || bytesRecorded <= 0) return;

                lock (_screenAudioLock)
                {
                    if (!IsSharingScreenLocal) return;

                    if (_screenAudioAccumulatorOffset + bytesRecorded <= _screenAudioAccumulator.Length)
                    {
                        System.Buffer.BlockCopy(audioData, 0, _screenAudioAccumulator, _screenAudioAccumulatorOffset, bytesRecorded);
                        _screenAudioAccumulatorOffset += bytesRecorded;
                    }
                    else
                        _screenAudioAccumulatorOffset = 0;

                    while (_screenAudioAccumulatorOffset >= ScreenAudioFrameBytes)
                    {
                        short[] pcm = new short[ScreenAudioFrameSize];
                        System.Buffer.BlockCopy(_screenAudioAccumulator, 0, pcm, 0, ScreenAudioFrameBytes);

                        _screenAudioAccumulatorOffset -= ScreenAudioFrameBytes;
                        if (_screenAudioAccumulatorOffset > 0)
                            System.Buffer.BlockCopy(_screenAudioAccumulator, ScreenAudioFrameBytes, _screenAudioAccumulator, 0, _screenAudioAccumulatorOffset);

                        try
                        {
                            byte[] opus = new byte[1275];
                            int encoded = _screenAudioEncoder.Encode(pcm, ScreenAudioFrameSize, opus, opus.Length);
                            if (encoded > 0)
                                _screenNetworkService.BroadcastScreenAudio(opus, encoded);
                        }
                        catch
                        {
                            break;
                        }
                    }
                }
            }
        );
    }

    private void StopScreenSharingInternal()
    {
        IsSharingScreenLocal = false;

        lock (_screenAudioLock)
        {
            _screenAudioAccumulatorOffset = 0;
        }

        _screenCaptureService.StopCapture();

        if (IsInCall)
        {
            _discoveryService.CurrentState = UserState.InCall;
            _discoveryService.BroadcastImmediateState();

            var localUser = ConnectedParticipants.FirstOrDefault(p => p.IsLocalUser);
            if (localUser != null) localUser.State = UserState.InCall;
        }

        CheckScreenStreamPresence();
    }

    [RelayCommand]
    public void StopScreenShare()
    {
        StopScreenSharingInternal();
    }

    [RelayCommand]
    public void ToggleScreenShareFullscreen()
    {
        if (IsSharingScreenLocal) return;

        IsScreenShareFullscreen = !IsScreenShareFullscreen;
    }

    private void UpdateCallStatusMessage()
    {
        if (!IsInCall)
        {
            int others = ConnectedParticipants.Count(p => !p.IsLocalUser);
            if (others > 0)
            {
                StatusText = $"Chamada ativa ({others} na sala)";
                MainActionButtonText = "Entrar na chamada";
            }
            else
            {
                StatusText = "Nenhuma chamada na rede";
                MainActionButtonText = "Iniciar chamada";
            }
        }
        else
        {
            int total = ConnectedParticipants.Count;
            StatusText = total > 1 ? $"Em chamada ({total} participantes)" : "Em chamada (Aguardando outros entrarem...)";
        }
    }

    private void StartVadDecayLoop()
    {
        _vadDecayCts?.Cancel();
        _vadDecayCts = new CancellationTokenSource();
        var token = _vadDecayCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(50, token);
                var now = DateTime.Now;

                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (IsInCall)
                    {
                        foreach (var participant in ConnectedParticipants)
                        {
                            if (participant.IsSpeaking && (now - participant.LastSpokeTime).TotalMilliseconds > 400)
                                participant.IsSpeaking = false;
                        }
                    }
                });
            }
        }, token);
    }

    private void LoadDevices(string? savedInId, string? savedOutId)
    {
        var inDevices = _audioService.GetInputDevices();
        AvailableMicrophones.Clear();
        foreach (var dev in inDevices) AvailableMicrophones.Add(dev);
        SelectedMicrophone = AvailableMicrophones.FirstOrDefault(d => d.ID == savedInId) ?? AvailableMicrophones.FirstOrDefault();

        var outDevices = _audioService.GetOutputDevices();
        AvailableOutputDevices.Clear();
        foreach (var dev in outDevices) AvailableOutputDevices.Add(dev);
        SelectedOutputDevice = AvailableOutputDevices.FirstOrDefault(d => d.ID == savedOutId) ?? AvailableOutputDevices.FirstOrDefault();
    }

    private void PersistSettings()
    {
        _settingsService.SaveSettings(new AppSettings
        {
            Username = Username,
            LocalPort = LocalPort,
            SelectedMicrophoneId = SelectedMicrophone?.ID,
            SelectedOutputDeviceId = SelectedOutputDevice?.ID,
            InputVolume = InputVolume,
            OutputVolume = OutputVolume,
            EnableNoiseSuppression = EnableNoiseSuppression,
            NoiseSuppressionLevel = NoiseSuppressionLevel,
            GateSensitivity = GateSensitivity
        });
    }

    partial void OnUsernameChanged(string value)
    {
        PersistSettings();
        _discoveryService.Username = value;
        var localUser = ConnectedParticipants.FirstOrDefault(p => p.IsLocalUser);
        if (localUser != null) localUser.Username = value;
    }

    partial void OnLocalPortChanged(string value)
    {
        PersistSettings();
        if (int.TryParse(value, out int p) && !IsInCall)
        {
            _discoveryService.Stop();
            _discoveryService.Start(Username, p);
        }
    }

    partial void OnInputVolumeChanged(double value)
    {
        _audioService.InputVolumeMultiplier = (float)(value / 100.0);
        PersistSettings();
    }

    partial void OnOutputVolumeChanged(double value)
    {
        _audioService.OutputVolumeMultiplier = (float)(value / 100.0);
        PersistSettings();
    }

    partial void OnEnableNoiseSuppressionChanged(bool value)
    {
        _audioService.EnableNoiseSuppression = value;
        PersistSettings();
    }

    partial void OnNoiseSuppressionLevelChanged(double value)
    {
        _audioService.NoiseSuppressionStrength = (float)(value / 100.0);
        PersistSettings();
    }

    partial void OnGateSensitivityChanged(double value)
    {
        _audioService.GateSensitivity = (float)(value / 100.0);
        PersistSettings();
    }

    partial void OnSelectedMicrophoneChanged(MMDevice? value)
    {
        PersistSettings();
        if (IsTestingMic)
        {
            _audioService.Stop();
            _audioService.StartLoopback(value?.ID, SelectedOutputDevice?.ID);
        }
    }

    partial void OnSelectedOutputDeviceChanged(MMDevice? value)
    {
        PersistSettings();
        if (IsTestingMic)
        {
            _audioService.Stop();
            _audioService.StartLoopback(SelectedMicrophone?.ID, value?.ID);
        }
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCallPageVisible));
        OnPropertyChanged(nameof(IsSettingsPageVisible));
    }

    [RelayCommand]
    private void ToggleTestMic()
    {
        if (!IsTestingMic)
        {
            _audioService.StartLoopback(SelectedMicrophone?.ID, SelectedOutputDevice?.ID);
            IsTestingMic = true;
            TestMicButtonText = "Parar teste";
        }
        else
        {
            _audioService.Stop();
            IsTestingMic = false;
            TestMicButtonText = "Testar microfone";
            VoiceLevel = 0;
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (IsDeafened) return;

        IsMuted = !IsMuted;
        _audioService.IsMuted = IsMuted;
        if (IsMuted)
        {
            var localUser = ConnectedParticipants.FirstOrDefault(p => p.IsLocalUser);
            if (localUser != null) localUser.IsSpeaking = false;
        }
    }

    [RelayCommand]
    private void ToggleDeafen()
    {
        IsDeafened = !IsDeafened;
        _audioService.IsDeafened = IsDeafened;

        if (IsDeafened)
        {
            IsMuted = true;
            _audioService.IsMuted = true;
            var localUser = ConnectedParticipants.FirstOrDefault(p => p.IsLocalUser);
            if (localUser != null) localUser.IsSpeaking = false;
        }
        else
        {
            IsMuted = false;
            _audioService.IsMuted = false;
        }
    }

    [RelayCommand]
    private void StartOrJoinCall()
    {
        if (IsTestingMic) ToggleTestMic();

        try
        {
            int localP = int.Parse(LocalPort);

            _udpClient.Start(localP);
            _screenNetworkService.Start(localP + 100);

            _audioService.StartNetworkStream((packet, length) =>
            {
                _udpClient.SendAudioFrame(packet, length);
            }, SelectedMicrophone?.ID, SelectedOutputDevice?.ID);

            _discoveryService.IsInCall = true;

            if (!ConnectedParticipants.Any(p => p.IsLocalUser))
            {
                ConnectedParticipants.Insert(0, new PeerInfo
                {
                    Username = Username,
                    IsLocalUser = true,
                    AudioPort = localP,
                    IpAddress = "127.0.0.1",
                    State = UserState.InCall
                });
            }

            foreach (var peer in ConnectedParticipants.Where(p => !p.IsLocalUser))
            {
                _udpClient.AddTarget(peer.IpAddress, peer.AudioPort);
                _screenNetworkService.AddTarget(peer.IpAddress, peer.AudioPort + 100);
            }

            _discoveryService.BroadcastImmediateState();

            StartVadDecayLoop();

            IsInCall = true;
            ParticipantsCount = ConnectedParticipants.Count;
            UpdateCallStatusMessage();
        }
        catch (Exception ex)
        {
            StatusText = $"Erro ao conectar: {ex.Message}";
        }
    }

    [RelayCommand]
    private void LeaveCall()
    {
        if (IsSharingScreenLocal)
            StopScreenSharingInternal();

        _vadDecayCts?.Cancel();
        _vadDecayCts = null;

        _audioService.Stop();
        _udpClient.Stop();
        _screenNetworkService.Stop();

        _discoveryService.IsInCall = false;
        _discoveryService.BroadcastImmediateState();

        foreach (var p in ConnectedParticipants)
        {
            p.IsSpeaking = false;
            string senderKey = $"{p.IpAddress}:{p.AudioPort}";
            _audioService.RemovePeerDecoder(senderKey);
        }

        var localUser = ConnectedParticipants.FirstOrDefault(p => p.IsLocalUser);
        if (localUser != null)
            ConnectedParticipants.Remove(localUser);

        IsInCall = false;
        HasActiveScreenStream = false;
        RemoteScreenImage = null;
        ParticipantsCount = ConnectedParticipants.Count;
        UpdateCallStatusMessage();
        VoiceLevel = 0;
    }
}