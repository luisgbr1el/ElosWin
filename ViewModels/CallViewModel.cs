using System;
using System.Collections.ObjectModel;
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
using ElosWin.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace ElosWin.ViewModels;

public partial class CallViewModel : ObservableObject
{
    private const int ScreenAudioSampleRate = 48000;
    private const int ScreenAudioChannels = 1;
    private const int ScreenAudioFrameSize = 960;
    private const int ScreenAudioFrameBytes = ScreenAudioFrameSize * sizeof(short);

    public IAudioService AudioService { get; }
    private readonly UdpVoiceClient _udpClient;
    private readonly PeerDiscoveryService _discoveryService;
    private readonly ScreenCaptureService _screenCaptureService;
    private readonly ScreenNetworkService _screenNetworkService;
    private readonly DispatcherQueue _dispatcherQueue;

    private readonly IOpusEncoder _screenAudioEncoder;
    private readonly object _screenAudioLock = new();
    private readonly byte[] _screenAudioAccumulator = new byte[ScreenAudioFrameBytes * 4];
    private int _screenAudioAccumulatorOffset = 0;
    private CancellationTokenSource? _vadDecayCts;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Nenhuma chamada na rede";

    [ObservableProperty]
    public partial string MainActionButtonText { get; set; } = "Iniciar chamada";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInCall))]
    [NotifyPropertyChangedFor(nameof(CanShowScreenShareFullscreenButton))]
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenShareButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(ScreenShareButtonToolTip))]
    [NotifyPropertyChangedFor(nameof(CanShowScreenShareFullscreenButton))]
    public partial bool IsSharingScreenLocal { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowScreenShareFullscreenButton))]
    public partial bool HasActiveScreenStream { get; set; } = false;

    [ObservableProperty]
    public partial bool IsScreenShareFullscreen { get; set; } = false;

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
    public Visibility CanShowScreenShareFullscreenButton => (IsInCall && HasActiveScreenStream && !IsSharingScreenLocal) ? Visibility.Visible : Visibility.Collapsed;

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

    public ObservableCollection<PeerInfo> ConnectedParticipants { get; } = new();

    public CallViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        AudioService = new WasapiAudioService();
        _udpClient = new UdpVoiceClient();
        _discoveryService = new PeerDiscoveryService();
        _screenCaptureService = new ScreenCaptureService();
        _screenNetworkService = new ScreenNetworkService();

        _screenAudioEncoder = OpusCodecFactory.CreateEncoder(ScreenAudioSampleRate, ScreenAudioChannels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _screenAudioEncoder.Bitrate = 96000;
        SelectedQuality = AvailableQualities[0];

        _udpClient.OnAudioPacketReceived += (opusPacket, senderEp) =>
        {
            if (!IsInCall) return;
            string senderIp = senderEp.Address.ToString();
            var remotePeer = ConnectedParticipants.FirstOrDefault(p => !p.IsLocalUser && (p.IpAddress == senderIp || p.AudioPort == senderEp.Port));
            if (remotePeer != null && remotePeer.IsLocallyMuted) return;

            string senderKey = senderEp.ToString();
            float peerVol = (remotePeer != null) ? (float)(remotePeer.UserVolume / 100.0) : 1.0f;
            float level = AudioService.PlayReceivedFrameFromSender(opusPacket, senderKey, peerVol);

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
            AudioService.PlayReceivedFrameFromSender(opusPacket, "screenshare_stream", streamVol);
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
                        AudioService.RemovePeerDecoder($"{peer.IpAddress}:{peer.AudioPort}");
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
                if (peer.State == UserState.SharingScreen && !peer.IsLocalUser && IsSharingScreenLocal)
                    StopScreenSharingInternal();
                CheckScreenStreamPresence();
            });
        };

        _discoveryService.OnCallStateUpdated += (peers) =>
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                ParticipantsCount = ConnectedParticipants.Count;
                UpdateCallStatusMessage();
                CheckScreenStreamPresence();
            });
        };
    }

    public void PrepareCaptureTargets()
    {
        AvailableCaptureTargets.Clear();
        foreach (var t in ScreenCaptureService.GetAvailableCaptureTargets()) AvailableCaptureTargets.Add(t);
        SelectedCaptureTarget = AvailableCaptureTargets.FirstOrDefault();
    }

    public void StartScreenSharingFromDialog()
    {
        if (!IsInCall || SelectedCaptureTarget == null) return;
        lock (_screenAudioLock) { _screenAudioAccumulatorOffset = 0; }

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
            (frameData) => { if (IsSharingScreenLocal) _screenNetworkService.BroadcastVideoFrame(frameData); },
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
                    else _screenAudioAccumulatorOffset = 0;

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
                            if (encoded > 0) _screenNetworkService.BroadcastScreenAudio(opus, encoded);
                        }
                        catch { break; }
                    }
                }
            }
        );
    }

    private void StopScreenSharingInternal()
    {
        IsSharingScreenLocal = false;
        lock (_screenAudioLock) { _screenAudioAccumulatorOffset = 0; }
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
    public void StopScreenShare() => StopScreenSharingInternal();

    [RelayCommand]
    public void ToggleScreenShareFullscreen()
    {
        if (IsSharingScreenLocal || !IsInCall) return;
        IsScreenShareFullscreen = !IsScreenShareFullscreen;
    }

    [RelayCommand]
    public void ToggleMute()
    {
        if (IsDeafened) return;
        IsMuted = !IsMuted;
        AudioService.IsMuted = IsMuted;
        if (IsMuted)
        {
            var localUser = ConnectedParticipants.FirstOrDefault(p => p.IsLocalUser);
            if (localUser != null) localUser.IsSpeaking = false;
        }
    }

    [RelayCommand]
    public void ToggleDeafen()
    {
        IsDeafened = !IsDeafened;
        AudioService.IsDeafened = IsDeafened;
        if (IsDeafened)
        {
            IsMuted = true;
            AudioService.IsMuted = true;
            var localUser = ConnectedParticipants.FirstOrDefault(p => p.IsLocalUser);
            if (localUser != null) localUser.IsSpeaking = false;
        }
        else
        {
            IsMuted = false;
            AudioService.IsMuted = false;
        }
    }

    [RelayCommand]
    public void StartOrJoinCall()
    {
        try
        {
            int localP = int.Parse(MainWindow.SharedSettingsVm.LocalPort);
            _udpClient.Start(localP);
            _screenNetworkService.Start(localP + 100);

            AudioService.StartNetworkStream((packet, length) =>
            {
                _udpClient.SendAudioFrame(packet, length);
            }, MainWindow.SharedSettingsVm.SelectedMicrophone?.ID, MainWindow.SharedSettingsVm.SelectedOutputDevice?.ID);

            _discoveryService.IsInCall = true;

            if (!ConnectedParticipants.Any(p => p.IsLocalUser))
            {
                ConnectedParticipants.Insert(0, new PeerInfo
                {
                    Username = MainWindow.SharedSettingsVm.Username,
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
    public void LeaveCall()
    {
        if (IsSharingScreenLocal) StopScreenSharingInternal();
        _vadDecayCts?.Cancel();
        _vadDecayCts = null;

        AudioService.Stop();
        _udpClient.Stop();
        _screenNetworkService.Stop();

        _discoveryService.IsInCall = false;
        _discoveryService.BroadcastImmediateState();

        foreach (var p in ConnectedParticipants)
        {
            p.IsSpeaking = false;
            AudioService.RemovePeerDecoder($"{p.IpAddress}:{p.AudioPort}");
        }

        var localUser = ConnectedParticipants.FirstOrDefault(p => p.IsLocalUser);
        if (localUser != null) ConnectedParticipants.Remove(localUser);

        IsInCall = false;
        IsScreenShareFullscreen = false;
        HasActiveScreenStream = false;
        RemoteScreenImage = null;
        ParticipantsCount = ConnectedParticipants.Count;
        UpdateCallStatusMessage();
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
}