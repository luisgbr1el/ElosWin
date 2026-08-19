using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

#pragma warning disable CS0618

namespace ElosWin.Services.Audio;

public class WasapiAudioService : IAudioService
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSize = 960;
    private const int FrameBytes = FrameSize * sizeof(short);

    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _waveProvider;

    private readonly IOpusEncoder _encoder;
    private readonly ConcurrentDictionary<string, IOpusDecoder> _peerDecoders = new();
    private readonly ConcurrentDictionary<string, bool> _peerBuffered = new();
    private readonly SpectralNoiseSuppressor _noiseFilter = new();

    private readonly byte[] _inputAccumulator = new byte[FrameBytes * 8];
    private int _inputAccumulatorOffset = 0;
    private readonly object _audioLock = new();

    private Action<byte[], int>? _onFrameCaptured;
    private bool _isLoopbackMode = false;
    private long _lastVadDispatchTicks = 0;

    public bool IsRunning { get; private set; }
    public bool IsMuted { get; set; } = false;
    public bool IsDeafened { get; set; } = false;
    public float InputVolumeMultiplier { get; set; } = 1.0f;
    public float OutputVolumeMultiplier { get; set; } = 1.0f;
    public bool EnableNoiseSuppression { get; set; } = true;

    public float NoiseSuppressionStrength
    {
        get => _noiseFilter.SuppressionStrength;
        set => _noiseFilter.SuppressionStrength = Math.Clamp(value, 0.0f, 1.0f);
    }

    public float GateSensitivity { get; set; } = 0.4f;

    public event Action<float>? OnVoiceLevelChanged;

    public WasapiAudioService()
    {
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 64000;
        _encoder.Complexity = 10;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
    }

    public List<MMDevice> GetInputDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
    }

    public List<MMDevice> GetOutputDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    public void StartLoopback(string? inputDeviceId = null, string? outputDeviceId = null)
    {
        _isLoopbackMode = true;
        _onFrameCaptured = null;
        InitWasapi(inputDeviceId, outputDeviceId);
    }

    public void StartNetworkStream(Action<byte[], int> onFrameCaptured, string? inputDeviceId = null, string? outputDeviceId = null)
    {
        _isLoopbackMode = false;
        _onFrameCaptured = onFrameCaptured;
        InitWasapi(inputDeviceId, outputDeviceId);
    }

    public void PlayReceivedFrame(byte[] opusPacket)
    {
        PlayReceivedFrameFromSender(opusPacket, "default_loopback", 1.0f);
    }

    public float PlayReceivedFrameFromSender(byte[] opusPacket, string senderKey, float individualVolume = 1.0f)
    {
        if (IsDeafened || _waveProvider == null) return 0f;

        var decoder = _peerDecoders.GetOrAdd(senderKey, _ => OpusCodecFactory.CreateDecoder(SampleRate, Channels));

        short[] decodedPcm = new short[FrameSize];
        ReadOnlySpan<byte> encodedSpan = new ReadOnlySpan<byte>(opusPacket);

        int decodedSamples;
        try
        {
            decodedSamples = decoder.Decode(encodedSpan, decodedPcm, FrameSize, false);
        }
        catch
        {
            decodedSamples = decoder.Decode(ReadOnlySpan<byte>.Empty, decodedPcm, FrameSize, false);
        }

        if (decodedSamples <= 0) return 0f;

        float effectiveVol = OutputVolumeMultiplier * individualVolume;
        float maxSample = 0f;

        for (int i = 0; i < decodedSamples; i++)
        {
            float processed = decodedPcm[i] * effectiveVol;
            short clamped = (short)Math.Clamp((int)processed, short.MinValue, short.MaxValue);
            decodedPcm[i] = clamped;

            float abs = Math.Abs(clamped);
            if (abs > maxSample) maxSample = abs;
        }

        byte[] pcmBytes = new byte[decodedSamples * sizeof(short)];
        Buffer.BlockCopy(decodedPcm, 0, pcmBytes, 0, pcmBytes.Length);

        if (!_peerBuffered.ContainsKey(senderKey))
        {
            _peerBuffered.TryAdd(senderKey, true);
            byte[] silence = new byte[FrameBytes * 2];
            _waveProvider.AddSamples(silence, 0, silence.Length);
        }

        _waveProvider.AddSamples(pcmBytes, 0, pcmBytes.Length);

        return maxSample / 32768f;
    }

    private void InitWasapi(string? inputDeviceId, string? outputDeviceId)
    {
        if (IsRunning) return;

        _inputAccumulatorOffset = 0;
        _peerDecoders.Clear();
        _peerBuffered.Clear();

        var waveFormat = new WaveFormat(SampleRate, 16, Channels);

        _waveProvider = new BufferedWaveProvider(waveFormat)
        {
            DiscardOnBufferOverflow = true
        };

        var enumerator = new MMDeviceEnumerator();

        MMDevice? selectedOutDevice = null;
        if (!string.IsNullOrEmpty(outputDeviceId))
            try { selectedOutDevice = enumerator.GetDevice(outputDeviceId); } catch { }

        _output = selectedOutDevice != null
            ? new WasapiOut(selectedOutDevice, AudioClientShareMode.Shared, true, 60)
            : new WasapiOut(AudioClientShareMode.Shared, 60);

        _output.Init(_waveProvider);

        MMDevice? selectedInDevice = null;
        if (!string.IsNullOrEmpty(inputDeviceId))
            try { selectedInDevice = enumerator.GetDevice(inputDeviceId); } catch { }

        _capture = selectedInDevice != null
            ? new WasapiCapture(selectedInDevice)
            : new WasapiCapture();

        _capture.WaveFormat = waveFormat;
        _capture.ShareMode = AudioClientShareMode.Shared;
        _capture.DataAvailable += OnAudioDataAvailable;

        _capture.StartRecording();
        _output.Play();

        IsRunning = true;
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        lock (_audioLock)
        {
            if (_inputAccumulatorOffset + e.BytesRecorded <= _inputAccumulator.Length)
            {
                Buffer.BlockCopy(e.Buffer, 0, _inputAccumulator, _inputAccumulatorOffset, e.BytesRecorded);
                _inputAccumulatorOffset += e.BytesRecorded;
            }
            else
                _inputAccumulatorOffset = 0;

            while (_inputAccumulatorOffset >= FrameBytes)
            {
                short[] pcmBuffer = new short[FrameSize];
                Buffer.BlockCopy(_inputAccumulator, 0, pcmBuffer, 0, FrameBytes);

                _inputAccumulatorOffset -= FrameBytes;
                if (_inputAccumulatorOffset > 0)
                    Buffer.BlockCopy(_inputAccumulator, FrameBytes, _inputAccumulator, 0, _inputAccumulatorOffset);

                if (EnableNoiseSuppression)
                    _noiseFilter.Process(pcmBuffer, FrameSize, true);

                float inVol = InputVolumeMultiplier;
                float maxSample = 0f;

                for (int i = 0; i < pcmBuffer.Length; i++)
                {
                    float finalSample = pcmBuffer[i] * inVol;
                    short clamped = (short)Math.Clamp((int)finalSample, short.MinValue, short.MaxValue);
                    pcmBuffer[i] = clamped;

                    float abs = Math.Abs(clamped);
                    if (abs > maxSample) maxSample = abs;
                }

                long nowTicks = Stopwatch.GetTimestamp();
                if ((nowTicks - _lastVadDispatchTicks) > (Stopwatch.Frequency / 40))
                {
                    _lastVadDispatchTicks = nowTicks;
                    float level = (IsMuted || IsDeafened) ? 0f : (maxSample / 32768f);
                    OnVoiceLevelChanged?.Invoke(level);
                }

                if (IsMuted || IsDeafened)
                    Array.Clear(pcmBuffer, 0, pcmBuffer.Length);

                byte[] opusPacket = new byte[1275];
                int encodedBytes = _encoder.Encode(pcmBuffer, FrameSize, opusPacket, opusPacket.Length);

                if (_isLoopbackMode)
                {
                    if (!IsDeafened)
                    {
                        byte[] localPacket = new byte[encodedBytes];
                        Array.Copy(opusPacket, localPacket, encodedBytes);
                        PlayReceivedFrame(localPacket);
                    }
                }
                else
                    _onFrameCaptured?.Invoke(opusPacket, encodedBytes);
            }
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;

        try
        {
            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;

            _output?.Stop();
            _output?.Dispose();
            _output = null;

            _waveProvider?.ClearBuffer();
            _waveProvider = null;

            _peerDecoders.Clear();
            _peerBuffered.Clear();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Erro ao parar áudio: {ex.Message}");
        }

        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
        _noiseFilter.Dispose();
    }
}