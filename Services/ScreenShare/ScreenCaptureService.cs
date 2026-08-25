using ElosWin.Models;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ElosWin.Services.ScreenShare;

public class ScreenCaptureService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out bool pvAttribute, int cbAttribute);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private CancellationTokenSource? _captureCts;
    private WasapiLoopbackCapture? _systemAudioCapture;

    public bool IsCapturing { get; private set; }

    public static List<CaptureTargetItem> GetAvailableCaptureTargets()
    {
        var list = new List<CaptureTargetItem>
        {
            new CaptureTargetItem
            {
                Title = "Tela inteira (Monitor principal)",
                Hwnd = IntPtr.Zero,
                IsFullScreen = true
            }
        };

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out bool isCloaked, sizeof(bool)) == 0 && isCloaked)
                return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            var builder = new StringBuilder(length + 1);
            GetWindowText(hWnd, builder, builder.Capacity);
            string title = builder.ToString().Trim();

            if (!string.IsNullOrWhiteSpace(title) &&
                title != "Elos" &&
                title != "Program Manager" &&
                title != "Settings" &&
                title != "Windows Input Experience" &&
                title != "Experiência de Entrada do Windows")
            {
                GetWindowRect(hWnd, out RECT r);
                int width = r.Right - r.Left;
                int height = r.Bottom - r.Top;

                if (width > 120 && height > 120)
                {
                    list.Add(new CaptureTargetItem
                    {
                        Title = title,
                        Hwnd = hWnd,
                        IsFullScreen = false
                    });
                }
            }
            return true;
        }, IntPtr.Zero);

        return list;
    }

    public void StartCapture(
        CaptureTargetItem target,
        ScreenShareQuality quality,
        bool captureAudio,
        Action<byte[]> onVideoChunkReady,
        Action<byte[], int>? onAudioDataReady)
    {
        if (IsCapturing) StopCapture();

        IsCapturing = true;
        _captureCts = new CancellationTokenSource();
        var token = _captureCts.Token;

        if (captureAudio && onAudioDataReady != null)
        {
            try
            {
                _systemAudioCapture = new WasapiLoopbackCapture();

                var waveFormat = _systemAudioCapture.WaveFormat;
                bool isFloat = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat || waveFormat.BitsPerSample == 32;
                int srcChannels = Math.Max(1, waveFormat.Channels);

                _systemAudioCapture.DataAvailable += (s, a) =>
                {
                    if (a.BytesRecorded == 0) return;

                    if (isFloat)
                    {
                        int floatSamples = a.BytesRecorded / 4;
                        int monoSamples = floatSamples / srcChannels;
                        byte[] pcm16Data = new byte[monoSamples * sizeof(short)];

                        int pcmIdx = 0;
                        for (int i = 0; i < a.BytesRecorded; i += (4 * srcChannels))
                        {
                            float mixedSample = 0f;
                            for (int ch = 0; ch < srcChannels; ch++)
                            {
                                int offset = i + (ch * 4);
                                if (offset + 4 <= a.BytesRecorded)
                                    mixedSample += BitConverter.ToSingle(a.Buffer, offset);
                            }
                            mixedSample /= srcChannels;

                            short s16 = (short)Math.Clamp((int)(mixedSample * 32767f), short.MinValue + 1, short.MaxValue);
                            pcm16Data[pcmIdx++] = (byte)(s16 & 0xFF);
                            pcm16Data[pcmIdx++] = (byte)((s16 >> 8) & 0xFF);
                        }

                        onAudioDataReady(pcm16Data, pcm16Data.Length);
                    }
                    else
                    {
                        onAudioDataReady(a.Buffer, a.BytesRecorded);
                    }
                };

                _systemAudioCapture.StartRecording();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenAudioCapture] Erro: {ex.Message}");
            }
        }

        Task.Run(async () =>
        {
            var jpgEncoder = GetEncoder(ImageFormat.Jpeg);
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality.JpegQuality);

            int targetDelay = 1000 / Math.Max(1, quality.Fps);

            while (!token.IsCancellationRequested)
            {
                var start = DateTime.UtcNow;

                byte[]? frameData = CaptureTargetToJpeg(target, quality.Width, quality.Height, jpgEncoder, encParams);
                if (frameData != null && frameData.Length > 0)
                {
                    onVideoChunkReady(frameData);
                }

                int elapsed = (int)(DateTime.UtcNow - start).TotalMilliseconds;
                int sleep = Math.Max(1, targetDelay - elapsed);
                await Task.Delay(sleep, token).ConfigureAwait(false);
            }
        }, token);
    }

    private byte[]? CaptureTargetToJpeg(CaptureTargetItem target, int maxW, int maxH, ImageCodecInfo? encoder, EncoderParameters encParams)
    {
        try
        {
            Bitmap? capturedBmp = null;

            if (target.IsFullScreen || target.Hwnd == IntPtr.Zero)
            {
                int screenW = GetSystemMetrics(SM_CXSCREEN);
                int screenH = GetSystemMetrics(SM_CYSCREEN);
                capturedBmp = new Bitmap(screenW, screenH, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(capturedBmp))
                {
                    g.CopyFromScreen(0, 0, 0, 0, new Size(screenW, screenH), CopyPixelOperation.SourceCopy);
                }
            }
            else
            {
                if (!GetWindowRect(target.Hwnd, out RECT rect)) return null;
                int w = Math.Max(1, rect.Right - rect.Left);
                int h = Math.Max(1, rect.Bottom - rect.Top);

                capturedBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(capturedBmp))
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(target.Hwnd, hdc, 2);
                    g.ReleaseHdc(hdc);
                }
            }

            if (capturedBmp == null) return null;

            using (capturedBmp)
            {
                float ratioX = (float)maxW / capturedBmp.Width;
                float ratioY = (float)maxH / capturedBmp.Height;
                float ratio = Math.Min(ratioX, ratioY);

                int targetW = Math.Max(1, (int)(capturedBmp.Width * ratio));
                int targetH = Math.Max(1, (int)(capturedBmp.Height * ratio));

                using var resized = new Bitmap(targetW, targetH);
                using (var g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(capturedBmp, 0, 0, targetW, targetH);
                }

                using var ms = new MemoryStream();
                if (encoder != null)
                    resized.Save(ms, encoder, encParams);
                else
                    resized.Save(ms, ImageFormat.Jpeg);

                return ms.ToArray();
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        var codecs = ImageCodecInfo.GetImageDecoders();
        foreach (var codec in codecs)
        {
            if (codec.FormatID == format.Guid)
                return codec;
        }
        return null;
    }

    public void StopCapture()
    {
        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _captureCts = null;

        try
        {
            _systemAudioCapture?.StopRecording();
            _systemAudioCapture?.Dispose();
            _systemAudioCapture = null;
        }
        catch { }

        IsCapturing = false;
    }

    public void Dispose()
    {
        StopCapture();
    }
}