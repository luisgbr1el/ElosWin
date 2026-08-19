using System;

namespace ElosWin.Services.Audio;

public class SpectralNoiseSuppressor
{
    private const int FftSize = 1024;
    private const int HalfFft = FftSize / 2;
    private const int HopSize = 480; // 10ms a 48kHz

    private readonly float[] _inputBuffer = new float[FftSize];
    private readonly float[] _outputBuffer = new float[FftSize * 2];
    private readonly float[] _window = new float[FftSize];

    private readonly float[] _noisePower = new float[HalfFft + 1];
    private readonly float[] _prevGain = new float[HalfFft + 1];

    private bool _noiseInitialized = false;
    private int _noiseInitFrames = 0;

    public float SuppressionStrength { get; set; } = 0.75f; // 0.0 a 1.0

    public SpectralNoiseSuppressor()
    {
        // Janela de Hann para análise espectral sem descontinuidade
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
        }

        for (int i = 0; i <= HalfFft; i++)
        {
            _prevGain[i] = 1.0f;
            _noisePower[i] = 1000f;
        }
    }

    public void Process(short[] pcmBuffer, int sampleCount, bool isSpeechActive)
    {
        // Processa em blocos de 480 amostras com 50% de overlap-add
        for (int offset = 0; offset < sampleCount; offset += HopSize)
        {
            int currentHop = Math.Min(HopSize, sampleCount - offset);
            ProcessHop(pcmBuffer, offset, currentHop, isSpeechActive);
        }
    }

    private void ProcessHop(short[] pcmBuffer, int offset, int count, bool isSpeechActive)
    {
        // Desloca o buffer de entrada
        Array.Copy(_inputBuffer, count, _inputBuffer, 0, FftSize - count);
        for (int i = 0; i < count; i++)
        {
            _inputBuffer[FftSize - count + i] = pcmBuffer[offset + i];
        }

        // Aplica janela de Hann
        float[] real = new float[FftSize];
        float[] imag = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            real[i] = _inputBuffer[i] * _window[i];
            imag[i] = 0f;
        }

        // FFT direta
        Fft(real, imag, false);

        // Subtração Espectral e Filtro Wiener
        float alpha = 1.0f + (SuppressionStrength * 2.5f); // Fator de sobre-subtração
        float beta = 0.02f + ((1.0f - SuppressionStrength) * 0.08f); // Piso espectral (evita som metálico)

        for (int k = 0; k <= HalfFft; k++)
        {
            float power = (real[k] * real[k]) + (imag[k] * imag[k]);

            // Atualiza perfil de ruído estático nas pausas da fala
            if (!isSpeechActive || !_noiseInitialized)
            {
                _noisePower[k] = (_noisePower[k] * 0.92f) + (power * 0.08f);
                if (++_noiseInitFrames > 30) _noiseInitialized = true;
            }

            float currentNoise = _noisePower[k];
            float snr = (power - (alpha * currentNoise)) / Math.Max(power, 1e-6f);
            float gain = Math.Clamp(snr, beta, 1.0f);

            // Suavização temporal entre frames para evitar "musical noise"
            gain = (_prevGain[k] * 0.4f) + (gain * 0.6f);
            _prevGain[k] = gain;

            real[k] *= gain;
            imag[k] *= gain;

            if (k > 0 && k < HalfFft)
            {
                real[FftSize - k] = real[k];
                imag[FftSize - k] = -imag[k];
            }
        }

        // IFFT (Transformada Inversa)
        Fft(real, imag, true);

        // Overlap-Add no buffer de saída
        for (int i = 0; i < FftSize; i++)
        {
            _outputBuffer[i] += real[i] * _window[i];
        }

        // Copia de volta para o buffer PCM
        for (int i = 0; i < count; i++)
        {
            float sample = _outputBuffer[i];
            pcmBuffer[offset + i] = (short)Math.Clamp((int)sample, short.MinValue, short.MaxValue);
        }

        // Desloca o buffer de saída
        Array.Copy(_outputBuffer, count, _outputBuffer, 0, _outputBuffer.Length - count);
        Array.Clear(_outputBuffer, _outputBuffer.Length - count, count);
    }

    private static void Fft(float[] real, float[] imag, bool inverse)
    {
        int n = real.Length;
        int j = 0;

        for (int i = 0; i < n - 1; i++)
        {
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
            int k = n / 2;
            while (k <= j)
            {
                j -= k;
                k /= 2;
            }
            j += k;
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = 2.0 * Math.PI / len * (inverse ? 1 : -1);
            float wlen_r = (float)Math.Cos(angle);
            float wlen_i = (float)Math.Sin(angle);

            for (int i = 0; i < n; i += len)
            {
                float w_r = 1.0f;
                float w_i = 0.0f;

                for (int k = 0; k < len / 2; k++)
                {
                    int u = i + k;
                    int v = i + k + (len / 2);

                    float v_r = (real[v] * w_r) - (imag[v] * w_i);
                    float v_i = (real[v] * w_i) + (imag[v] * w_r);

                    real[v] = real[u] - v_r;
                    imag[v] = imag[u] - v_i;
                    real[u] += v_r;
                    imag[u] += v_i;

                    float next_w_r = (w_r * wlen_r) - (w_i * wlen_i);
                    w_i = (w_r * wlen_i) + (w_i * wlen_r);
                    w_r = next_w_r;
                }
            }
        }

        if (inverse)
        {
            for (int i = 0; i < n; i++)
            {
                real[i] /= n;
                imag[i] /= n;
            }
        }
    }
}