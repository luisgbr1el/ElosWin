using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ElosWin.Services.Network;

public class ScreenNetworkService : IDisposable
{
    private const int HeaderSize = 12;
    private const int MaxChunkSize = 1100;

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private int _localPort;

    private readonly ConcurrentDictionary<string, IPEndPoint> _targets = new();
    private readonly ConcurrentDictionary<uint, MemoryStream> _frameReassembler = new();
    private readonly ConcurrentDictionary<uint, int> _frameChunkCounts = new();

    private uint _frameSequenceCounter = 0;

    public bool IsRunning { get; private set; }
    public event Action<byte[]>? OnFrameReassembled;
    public event Action<byte[], IPEndPoint>? OnScreenAudioPacketReceived;

    public void Start(int localPort)
    {
        if (IsRunning) return;

        _localPort = localPort;
        _targets.Clear();
        _frameReassembler.Clear();

        _udpClient = new UdpClient();
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));

        _cts = new CancellationTokenSource();
        IsRunning = true;

        Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void AddTarget(string ip, int port)
    {
        string key = $"{ip}:{port}";
        if (IPAddress.TryParse(ip, out var parsedIp))
            _targets.TryAdd(key, new IPEndPoint(parsedIp, port));
    }

    public void RemoveTarget(string ip, int port)
    {
        string key = $"{ip}:{port}";
        _targets.TryRemove(key, out _);
    }

    public void ClearTargets()
    {
        _targets.Clear();
    }

    public void BroadcastVideoFrame(byte[] jpegBytes)
    {
        if (!IsRunning || _udpClient == null || _targets.IsEmpty) return;

        uint frameId = Interlocked.Increment(ref _frameSequenceCounter);
        int totalLength = jpegBytes.Length;
        int totalChunks = (int)Math.Ceiling((double)totalLength / MaxChunkSize);

        for (int i = 0; i < totalChunks; i++)
        {
            int offset = i * MaxChunkSize;
            int count = Math.Min(MaxChunkSize, totalLength - offset);

            byte[] packet = new byte[HeaderSize + count];
            packet[0] = (byte)'V';

            BitConverter.GetBytes(frameId).CopyTo(packet, 1);
            BitConverter.GetBytes((ushort)i).CopyTo(packet, 5);
            BitConverter.GetBytes((ushort)totalChunks).CopyTo(packet, 7);
            BitConverter.GetBytes((ushort)count).CopyTo(packet, 9);
            packet[11] = 0;

            Buffer.BlockCopy(jpegBytes, offset, packet, HeaderSize, count);

            foreach (var target in _targets.Values)
            {
                try
                {
                    _udpClient.Send(packet, packet.Length, target);
                }
                catch { }
            }
        }
    }

    public void BroadcastScreenAudio(byte[] opusPacket, int length)
    {
        if (!IsRunning || _udpClient == null || _targets.IsEmpty) return;

        byte[] packet = new byte[1 + length];
        packet[0] = (byte)'A';
        Buffer.BlockCopy(opusPacket, 0, packet, 1, length);

        foreach (var target in _targets.Values)
        {
            try
            {
                _udpClient.Send(packet, packet.Length, target);
            }
            catch { }
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                UdpReceiveResult result = await _udpClient.ReceiveAsync(token).ConfigureAwait(false);
                byte[] data = result.Buffer;

                if (data.Length < 1) continue;

                if (data[0] == 'A')
                {
                    byte[] audio = new byte[data.Length - 1];
                    Buffer.BlockCopy(data, 1, audio, 0, audio.Length);
                    OnScreenAudioPacketReceived?.Invoke(audio, result.RemoteEndPoint);
                }
                else if (data[0] == 'V' && data.Length >= HeaderSize)
                {
                    uint frameId = BitConverter.ToUInt32(data, 1);
                    ushort chunkIdx = BitConverter.ToUInt16(data, 5);
                    ushort totalChunks = BitConverter.ToUInt16(data, 7);
                    ushort payloadSize = BitConverter.ToUInt16(data, 9);

                    if (data.Length < HeaderSize + payloadSize) continue;

                    var ms = _frameReassembler.GetOrAdd(frameId, _ => new MemoryStream());
                    lock (ms)
                    {
                        ms.Position = chunkIdx * MaxChunkSize;
                        ms.Write(data, HeaderSize, payloadSize);
                    }

                    int count = _frameChunkCounts.AddOrUpdate(frameId, 1, (_, c) => c + 1);

                    if (count >= totalChunks)
                    {
                        if (_frameReassembler.TryRemove(frameId, out var completedMs))
                        {
                            _frameChunkCounts.TryRemove(frameId, out _);
                            byte[] completedFrame = completedMs.ToArray();
                            completedMs.Dispose();
                            OnFrameReassembled?.Invoke(completedFrame);
                        }

                        if (_frameReassembler.Count > 10)
                        {
                            _frameReassembler.Clear();
                            _frameChunkCounts.Clear();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _cts?.Cancel();
        _udpClient?.Close();
        _udpClient?.Dispose();
        _cts = null;
        _udpClient = null;
        _targets.Clear();
        _frameReassembler.Clear();
        _frameChunkCounts.Clear();
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
    }
}