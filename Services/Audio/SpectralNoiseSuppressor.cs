using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ElosWin.Services.Audio;

public class SpectralNoiseSuppressor : IDisposable
{
    private const int ChunkSize = 480;
    private InferenceSession? _session;
    private bool _hasModel = false;

    private string _audioInputName = "input_frame";
    private string _statesInputName = "states";
    private string? _attenLimInputName = null;
    private string _statesOutputName = "out_states";
    private DenseTensor<float>? _rnnStateTensor;
    private readonly object _lock = new();

    private float _x1, _x2, _y1, _y2;
    private readonly float _b0, _b1, _b2, _a1, _a2;

    private float _prevSample = 0f;
    private float _clickSmoothing = 0f;

    private float _envelope = 0f;
    private float _currentGain = 1f;

    public float SuppressionStrength { get; set; } = 0.85f;

    public SpectralNoiseSuppressor()
    {
        double f0 = 90.0;
        double fs = 48000.0;
        double w0 = 2.0 * Math.PI * f0 / fs;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * 0.7071);

        double a0 = 1.0 + alpha;
        _b0 = (float)((1.0 + cosW0) / 2.0 / a0);
        _b1 = (float)(-(1.0 + cosW0) / a0);
        _b2 = (float)((1.0 + cosW0) / 2.0 / a0);
        _a1 = (float)(-2.0 * cosW0 / a0);
        _a2 = (float)((1.0 - alpha) / a0);

        try
        {
            string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "denoiser.onnx");
            if (File.Exists(modelPath))
            {
                var options = new SessionOptions();
                options.AppendExecutionProvider_CPU(1);
                _session = new InferenceSession(modelPath, options);

                var inputMetadata = _session.InputMetadata;

                var attenKey = inputMetadata.Keys.FirstOrDefault(k =>
                    k.Contains("atten", StringComparison.OrdinalIgnoreCase) ||
                    k.Contains("db", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(attenKey))
                    _attenLimInputName = attenKey;

                var audioKey = inputMetadata.Keys.FirstOrDefault(k =>
                    k.Equals("input_frame", StringComparison.OrdinalIgnoreCase) ||
                    k.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                    (k.Contains("input", StringComparison.OrdinalIgnoreCase) && k != _attenLimInputName));

                if (!string.IsNullOrEmpty(audioKey))
                    _audioInputName = audioKey;

                var stateKey = inputMetadata.Keys.FirstOrDefault(k =>
                    k.Equals("states", StringComparison.OrdinalIgnoreCase) ||
                    k.Contains("state", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(stateKey) && inputMetadata.TryGetValue(stateKey, out var stateMeta))
                {
                    _statesInputName = stateKey;
                    int[] rawDims = stateMeta.Dimensions;
                    int[] safeDims = rawDims.Select(d => d > 0 ? d : 1).ToArray();
                    if (safeDims.Length == 0) safeDims = new[] { 1, 128 };
                    _rnnStateTensor = new DenseTensor<float>(safeDims);
                }
                else
                {
                    var remainingKey = inputMetadata.Keys.FirstOrDefault(k => k != _audioInputName && k != _attenLimInputName);
                    if (!string.IsNullOrEmpty(remainingKey))
                    {
                        _statesInputName = remainingKey;
                        int[] safeDims = inputMetadata[remainingKey].Dimensions.Select(d => d > 0 ? d : 1).ToArray();
                        if (safeDims.Length == 0) safeDims = new[] { 1, 128 };
                        _rnnStateTensor = new DenseTensor<float>(safeDims);
                    }
                }

                var outputMetadata = _session.OutputMetadata;
                var outStateKey = outputMetadata.Keys.FirstOrDefault(k =>
                    k.Contains("state", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(outStateKey))
                    _statesOutputName = outStateKey;

                _hasModel = true;
                System.Diagnostics.Debug.WriteLine($"[ONNX RNN] Carregado. Audio: '{_audioInputName}', States: '{_statesInputName}', AttenLim: '{_attenLimInputName ?? "None"}'");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ONNX] Erro ao carregar modelo: {ex.Message}");
            _hasModel = false;
        }
    }

    public void Process(short[] pcmBuffer, int sampleCount, bool isSpeechActive)
    {
        lock (_lock)
        {
            SuppressClickTransients(pcmBuffer, sampleCount);

            if (_hasModel && _session != null)
                ProcessWithRnnOnnx(pcmBuffer, sampleCount);
            else
                ProcessWithNeuralGain(pcmBuffer, sampleCount);

            ApplyNoiseGatePostProcess(pcmBuffer, sampleCount);
        }
    }

    private void SuppressClickTransients(short[] pcmBuffer, int sampleCount)
    {
        float thresholdDelta = 3200f;

        for (int i = 0; i < sampleCount; i++)
        {
            float current = pcmBuffer[i];
            float delta = Math.Abs(current - _prevSample);

            if (delta > thresholdDelta)
                _clickSmoothing = 0.55f;

            if (_clickSmoothing > 0.01f)
            {
                current = (_prevSample * 0.7f) + (current * 0.3f);
                _clickSmoothing *= 0.85f;
            }

            _prevSample = current;
            pcmBuffer[i] = (short)Math.Clamp((int)current, short.MinValue, short.MaxValue);
        }
    }

    private void ApplyNoiseGatePostProcess(short[] pcmBuffer, int sampleCount)
    {
        double sum = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            sum += pcmBuffer[i] * pcmBuffer[i];
        }

        double rms = Math.Sqrt(sum / sampleCount);
        float gateFloor = rms < 180 ? 0.0f : 1.0f;

        for (int i = 0; i < sampleCount; i++)
        {
            _currentGain += 0.1f * (gateFloor - _currentGain);
            pcmBuffer[i] = (short)(pcmBuffer[i] * _currentGain);
        }
    }

    private void ProcessWithRnnOnnx(short[] pcmBuffer, int sampleCount)
    {
        try
        {
            float strength = SuppressionStrength;

            for (int offset = 0; offset < sampleCount; offset += ChunkSize)
            {
                int len = Math.Min(ChunkSize, sampleCount - offset);
                if (len < ChunkSize) break;

                float[] floatChunk = new float[ChunkSize];
                for (int i = 0; i < ChunkSize; i++)
                {
                    floatChunk[i] = pcmBuffer[offset + i] / 32768.0f;
                }

                var audioTensor = new DenseTensor<float>(floatChunk, new[] { ChunkSize });

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_audioInputName, audioTensor)
                };

                if (_rnnStateTensor != null)
                    inputs.Add(NamedOnnxValue.CreateFromTensor(_statesInputName, _rnnStateTensor));

                if (!string.IsNullOrEmpty(_attenLimInputName))
                {
                    var attenDims = _session!.InputMetadata[_attenLimInputName].Dimensions;
                    int[] safeDims = attenDims.Length == 0 ? new[] { 1 } : attenDims.Select(d => d > 0 ? d : 1).ToArray();

                    var attenTensor = new DenseTensor<float>(safeDims);
                    for (int i = 0; i < attenTensor.Length; i++)
                    {
                        attenTensor.SetValue(i, -100.0f);
                    }

                    inputs.Add(NamedOnnxValue.CreateFromTensor(_attenLimInputName, attenTensor));
                }

                using var results = _session!.Run(inputs);

                DisposableNamedOnnxValue? audioResult = null;
                DisposableNamedOnnxValue? stateResult = null;

                foreach (var r in results)
                {
                    if (r.Name == _statesOutputName || r.Name.Contains("state", StringComparison.OrdinalIgnoreCase))
                        stateResult = r;
                    else if (audioResult == null)
                        audioResult = r;
                }

                if (stateResult != null)
                {
                    var newStates = stateResult.AsTensor<float>();
                    _rnnStateTensor = new DenseTensor<float>(newStates.ToArray(), newStates.Dimensions.ToArray());
                }

                if (audioResult != null)
                {
                    var outAudio = audioResult.AsTensor<float>();
                    int idx = 0;

                    foreach (var val in outAudio)
                    {
                        if (idx < ChunkSize)
                        {
                            float original = floatChunk[idx];
                            float denoised = val;
                            float mixed = (original * (1.0f - strength)) + (denoised * strength);

                            pcmBuffer[offset + idx] = (short)Math.Clamp((int)(mixed * 32768.0f), short.MinValue, short.MaxValue);
                            idx++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ONNX RNN Execution Error] {ex.Message}");
            _hasModel = false;
            ProcessWithNeuralGain(pcmBuffer, sampleCount);
        }
    }

    private void ProcessWithNeuralGain(short[] pcmBuffer, int sampleCount)
    {
        float attack = 0.12f;
        float release = 0.005f;
        float strength = SuppressionStrength;
        float threshold = 400f + (strength * 700f);
        float floor = Math.Max(0.02f, 1.0f - (strength * 0.95f));

        for (int i = 0; i < sampleCount; i++)
        {
            float input = pcmBuffer[i];

            float filtered = (_b0 * input) + (_b1 * _x1) + (_b2 * _x2) - (_a1 * _y1) - (_a2 * _y2);
            _x2 = _x1;
            _x1 = input;
            _y2 = _y1;
            _y1 = filtered;

            float abs = Math.Abs(filtered);
            if (abs > _envelope)
                _envelope += attack * (abs - _envelope);
            else
                _envelope += release * (abs - _envelope);

            float targetGain = 1.0f;
            if (_envelope < threshold)
            {
                float factor = _envelope / Math.Max(threshold, 1f);
                targetGain = floor + ((1.0f - floor) * factor * factor);
            }

            _currentGain += 0.08f * (targetGain - _currentGain);

            float outSample = filtered * _currentGain;
            pcmBuffer[i] = (short)Math.Clamp((int)outSample, short.MinValue, short.MaxValue);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}