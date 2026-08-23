using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ElosWin.Models;

namespace ElosWin.Services.Network;

public class ChatNetworkService : IDisposable
{
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private int _localPort;
    private readonly ConcurrentDictionary<string, IPEndPoint> _targets = new();
    private readonly ConcurrentDictionary<string, DateTime> _receivedMessageIds = new();

    public bool IsRunning { get; private set; }

    public event Action<ChatMessage, IPEndPoint>? OnMessageReceived;

    public void Start(int localPort)
    {
        if (IsRunning) return;

        _localPort = localPort;
        _targets.Clear();

        _udpClient = new UdpClient();
        _udpClient.EnableBroadcast = true;
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, localPort + 200));

        _cts = new CancellationTokenSource();
        IsRunning = true;

        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _udpClient != null)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(token);
                    string json = Encoding.UTF8.GetString(result.Buffer);

                    var message = JsonSerializer.Deserialize<ChatMessage>(json);
                    if (message != null && !string.IsNullOrWhiteSpace(message.Content))
                    {
                        // Deduplicação: ignora se já recebemos esse ID nos últimos 30 segundos
                        if (!string.IsNullOrEmpty(message.Id))
                        {
                            if (!_receivedMessageIds.TryAdd(message.Id, DateTime.Now))
                            {
                                continue;
                            }
                        }

                        message.IsFromSelf = false;
                        OnMessageReceived?.Invoke(message, result.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Erro no chat UDP: {ex.Message}");
                }
            }
        }, token);
    }

    private List<IPAddress> GetBroadcastAddresses()
    {
        var broadcastList = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ua.IPv4Mask != null)
                    {
                        byte[] ipBytes = ua.Address.GetAddressBytes();
                        byte[] maskBytes = ua.IPv4Mask.GetAddressBytes();

                        if (ipBytes.Length == 4 && maskBytes.Length == 4)
                        {
                            byte[] broadcastBytes = new byte[4];
                            for (int i = 0; i < 4; i++)
                                broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);

                            broadcastList.Add(new IPAddress(broadcastBytes));
                        }
                    }
                }
            }
        }
        catch { }

        return broadcastList.Distinct().ToList();
    }

    public async Task BroadcastMessageAsync(ChatMessage message)
    {
        if (!IsRunning || _udpClient == null || string.IsNullOrWhiteSpace(message.Content)) return;

        string json = JsonSerializer.Serialize(message);
        byte[] data = Encoding.UTF8.GetBytes(json);

        if (_targets.Count > 0)
        {
            foreach (var endpoint in _targets.Values)
            {
                try
                {
                    await _udpClient.SendAsync(data, data.Length, endpoint);
                }
                catch { }
            }
        }

        var broadcasts = GetBroadcastAddresses();
        foreach (var bcast in broadcasts)
        {
            try
            {
                var ep = new IPEndPoint(bcast, _localPort + 200);
                await _udpClient.SendAsync(data, data.Length, ep);
            }
            catch { }
        }
    }

    public void AddTarget(string ip, int basePort)
    {
        string key = $"{ip}:{basePort + 200}";

        if (IPAddress.TryParse(ip, out var parsedIp))
        {
            _targets.TryAdd(key, new IPEndPoint(parsedIp, basePort + 200));
        }
    }

    public void RemoveTarget(string ip, int basePort)
    {
        string key = $"{ip}:{basePort + 200}";
        _targets.TryRemove(key, out _);
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;

        _targets.Clear();
        _receivedMessageIds.Clear();
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}