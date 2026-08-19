using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElosWin.Models;
using ElosWin.Services.Audio;
using ElosWin.Services.Network;
using ElosWin.Services.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NAudio.CoreAudioApi;

namespace ElosWin.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAudioService _audioService;
    private readonly UdpVoiceClient _udpClient;
    private readonly PeerDiscoveryService _discoveryService;
    private readonly SettingsService _settingsService;
    private readonly DispatcherQueue _dispatcherQueue;
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
    public partial bool IsMuted { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeafenGlyph))]
    [NotifyPropertyChangedFor(nameof(CanToggleMute))]
    public partial bool IsDeafened { get; set; } = false;

    public bool CanToggleMute => !IsDeafened;
    public string MuteGlyph => IsMuted ? "\uF781" : "\uE720";
    public string DeafenGlyph => IsDeafened ? "\uE74F" : "\uE767";

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

        // VAD local
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

        // Recepção de áudio de outros participantes
        _udpClient.OnAudioPacketReceived += (opusPacket, senderEp) =>
        {
            if (!IsInCall) return;

            string senderKey = senderEp.ToString();
            _audioService.PlayReceivedFrameFromSender(opusPacket, senderKey);

            _dispatcherQueue.TryEnqueue(() =>
            {
                string senderIp = senderEp.Address.ToString();
                var remotePeer = ConnectedParticipants.FirstOrDefault(p => !p.IsLocalUser && (p.IpAddress == senderIp || p.AudioPort == senderEp.Port));
                if (remotePeer != null)
                {
                    remotePeer.IsSpeaking = true;
                    remotePeer.LastSpokeTime = DateTime.Now;
                }
            });
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
                    }
                }
                ParticipantsCount = ConnectedParticipants.Count;
                UpdateCallStatusMessage();
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
                        _udpClient.RemoveTarget(peer.IpAddress, peer.AudioPort);
                }
                ParticipantsCount = ConnectedParticipants.Count;
                UpdateCallStatusMessage();
            });
        };

        _discoveryService.OnCallStateUpdated += (peersInCallCount) =>
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                ParticipantsCount = ConnectedParticipants.Count;
                UpdateCallStatusMessage();
            });
        };

        LoadDevices(saved.SelectedMicrophoneId, saved.SelectedOutputDeviceId);

        if (int.TryParse(LocalPort, out int port))
        {
            _discoveryService.Start(Username, port);
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
                            {
                                participant.IsSpeaking = false;
                            }
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
                    IpAddress = "127.0.0.1"
                });
            }

            foreach (var peer in ConnectedParticipants.Where(p => !p.IsLocalUser))
            {
                _udpClient.AddTarget(peer.IpAddress, peer.AudioPort);
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
        _vadDecayCts?.Cancel();
        _vadDecayCts = null;

        _audioService.Stop();
        _udpClient.Stop();

        _discoveryService.IsInCall = false;
        _discoveryService.BroadcastImmediateState();

        foreach (var p in ConnectedParticipants)
        {
            p.IsSpeaking = false;
        }

        var localUser = ConnectedParticipants.FirstOrDefault(p => p.IsLocalUser);
        if (localUser != null)
            ConnectedParticipants.Remove(localUser);

        IsInCall = false;
        ParticipantsCount = ConnectedParticipants.Count;
        UpdateCallStatusMessage();
        VoiceLevel = 0;
    }
}