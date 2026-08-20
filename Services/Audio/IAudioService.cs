using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace ElosWin.Services.Audio;

public interface IAudioService : IDisposable
{
    void StartLoopback(string? inputDeviceId = null, string? outputDeviceId = null);
    void StartNetworkStream(Action<byte[], int> onFrameCaptured, string? inputDeviceId = null, string? outputDeviceId = null);
    void PlayReceivedFrame(byte[] opusPacket);
    float PlayReceivedFrameFromSender(byte[] opusPacket, string senderKey, float individualVolume = 1.0f);
    void RemovePeerDecoder(string senderKey);
    void Stop();

    bool IsRunning { get; }
    bool IsMuted { get; set; }
    bool IsDeafened { get; set; }

    float InputVolumeMultiplier { get; set; }
    float OutputVolumeMultiplier { get; set; }
    bool EnableNoiseSuppression { get; set; }
    float NoiseSuppressionStrength { get; set; }
    float GateSensitivity { get; set; }

    List<MMDevice> GetInputDevices();
    List<MMDevice> GetOutputDevices();
    event Action<float>? OnVoiceLevelChanged;
}