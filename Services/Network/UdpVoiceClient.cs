using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ElosWin.Services.Network;

public class UdpVoiceClient : IDisposable
{
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private int _currentLocalPort;

    private readonly ConcurrentDictionary<string, IPEndPoint> _callEndpoints = new();

    public bool IsListening { get; private set; }
    public event Action<byte[], IPEndPoint>? OnAudioPacketReceived;

    public void Start(int localPort)
    {
        if (IsListening) return;

        _currentLocalPort = localPort;
        _callEndpoints.Clear();

        _udpClient = new UdpClient();
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));

        _cts = new CancellationTokenSource();
        IsListening = true;

        Task.Run(() => ListenAsync(_cts.Token));
    }

    public void AddTarget(string ip, int port)
    {
        string key = $"{ip}:{port}";
        if (IPAddress.TryParse(ip, out var parsedIp))
            _callEndpoints.TryAdd(key, new IPEndPoint(parsedIp, port));
    }

    public void RemoveTarget(string ip, int port)
    {
        string key = $"{ip}:{port}";
        _callEndpoints.TryRemove(key, out _);
    }

    public void ClearTargets()
    {
        _callEndpoints.Clear();
    }

    public void SendAudioFrame(byte[] opusPacket, int length)
    {
        if (!IsListening || _udpClient == null || _callEndpoints.IsEmpty) return;

        try
        {
            byte[] dataToSend = opusPacket;
            if (opusPacket.Length != length)
            {
                dataToSend = new byte[length];
                Array.Copy(opusPacket, dataToSend, length);
            }

            foreach (var ep in _callEndpoints.Values)
            {
                _udpClient.Send(dataToSend, length, ep);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Erro ao enviar pacote UDP: {ex.Message}");
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                UdpReceiveResult result = await _udpClient.ReceiveAsync(token);

                if (result.RemoteEndPoint.Port == _currentLocalPort && IPAddress.IsLoopback(result.RemoteEndPoint.Address))
                    continue;

                OnAudioPacketReceived?.Invoke(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erro no socket UDP: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        if (!IsListening) return;

        _cts?.Cancel();
        _udpClient?.Close();
        _udpClient?.Dispose();
        _cts = null;
        _udpClient = null;
        _callEndpoints.Clear();
        IsListening = false;
    }

    public void Dispose()
    {
        Stop();
    }
}