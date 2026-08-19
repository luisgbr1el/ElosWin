using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElosWin.Models;

namespace ElosWin.Services.Network;

public class PeerDiscoveryService : IDisposable
{
    private const int DiscoveryPort = 5555;
    private const int PeerTimeoutSeconds = 6;

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;

    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly ConcurrentDictionary<string, PeerInfo> _activePeers = new();

    public string Username { get; set; } = Environment.MachineName;
    public int MyAudioPort { get; set; } = 5000;
    public bool IsInCall { get; set; } = false;
    public bool IsRunning { get; private set; }

    public event Action<PeerInfo>? OnPeerJoinedCall;
    public event Action<PeerInfo>? OnPeerLeftCall;
    public event Action<int>? OnCallStateUpdated;

    public List<PeerInfo> GetKnownPeersInCall()
    {
        return _activePeers.Values.ToList();
    }

    public void Start(string username, int myAudioPort)
    {
        if (IsRunning) return;

        Username = username;
        MyAudioPort = myAudioPort;
        _activePeers.Clear();
        _cts = new CancellationTokenSource();

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _udpClient.EnableBroadcast = true;

            IsRunning = true;

            Task.Run(() => ListenAsync(_cts.Token));
            Task.Run(() => BroadcastLoopAsync(_cts.Token));
            Task.Run(() => CleanupLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Erro ao iniciar discovery: {ex.Message}");
        }
    }

    public void BroadcastImmediateState()
    {
        if (_udpClient == null || !IsRunning) return;

        try
        {
            string state = IsInCall ? "IN_CALL" : "IDLE";
            string message = $"ELOS_PING|{_instanceId}|{Username}|{MyAudioPort}|{state}";
            byte[] bytes = Encoding.UTF8.GetBytes(message);

            var broadcastTargets = GetBroadcastAddresses();
            foreach (var target in broadcastTargets)
            {
                try
                {
                    _udpClient.Send(bytes, bytes.Length, new IPEndPoint(target, DiscoveryPort));
                }
                catch { }
            }
        }
        catch { }
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
                            {
                                broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                            }
                            broadcastList.Add(new IPAddress(broadcastBytes));
                        }
                    }
                }
            }
        }
        catch { }

        return broadcastList.Distinct().ToList();
    }

    private HashSet<string> GetAllLocalIPAddresses()
    {
        var localIps = new HashSet<string> { "127.0.0.1", "localhost" };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        localIps.Add(addr.Address.ToString());
                    }
                }
            }
        }
        catch { }
        return localIps;
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                string state = IsInCall ? "IN_CALL" : "IDLE";
                string message = $"ELOS_PING|{_instanceId}|{Username}|{MyAudioPort}|{state}";
                byte[] bytes = Encoding.UTF8.GetBytes(message);

                var broadcastTargets = GetBroadcastAddresses();
                foreach (var target in broadcastTargets)
                {
                    try
                    {
                        await _udpClient.SendAsync(bytes, bytes.Length, new IPEndPoint(target, DiscoveryPort));
                    }
                    catch { }
                }

                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erro no broadcast: {ex.Message}");
            }
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        var myIps = GetAllLocalIPAddresses();

        while (!token.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                UdpReceiveResult result = await _udpClient.ReceiveAsync(token);
                string raw = Encoding.UTF8.GetString(result.Buffer);

                string[] parts = raw.Split('|');
                if (parts.Length >= 5 && (parts[0] == "ELOS_PING" || parts[0] == "ELOS_PONG"))
                {
                    string packetType = parts[0];
                    string senderGuidStr = parts[1];
                    string peerUser = parts[2];

                    if (Guid.TryParse(senderGuidStr, out Guid senderGuid) && senderGuid == _instanceId)
                    {
                        continue;
                    }

                    if (int.TryParse(parts[3], out int peerAudioPort))
                    {
                        string peerIp = result.RemoteEndPoint.Address.ToString();
                        if (myIps.Contains(peerIp) && peerAudioPort == MyAudioPort)
                        {
                            continue;
                        }

                        string state = parts[4];
                        string peerKey = $"{peerIp}:{peerAudioPort}";

                        // Responde Unicast com PONG imediatamente para confirmar presença
                        if (packetType == "ELOS_PING" && _udpClient != null)
                        {
                            try
                            {
                                string myState = IsInCall ? "IN_CALL" : "IDLE";
                                string pong = $"ELOS_PONG|{_instanceId}|{Username}|{MyAudioPort}|{myState}";
                                byte[] pongBytes = Encoding.UTF8.GetBytes(pong);
                                _ = _udpClient.SendAsync(pongBytes, pongBytes.Length, result.RemoteEndPoint);
                            }
                            catch { }
                        }

                        if (state == "IN_CALL")
                        {
                            var peer = new PeerInfo
                            {
                                Username = peerUser,
                                IpAddress = peerIp,
                                AudioPort = peerAudioPort,
                                LastSeen = DateTime.Now
                            };

                            bool isNew = !_activePeers.ContainsKey(peerKey);
                            _activePeers.AddOrUpdate(peerKey, peer, (k, existing) =>
                            {
                                existing.LastSeen = DateTime.Now;
                                existing.Username = peerUser;
                                return existing;
                            });

                            if (isNew)
                            {
                                OnPeerJoinedCall?.Invoke(peer);
                            }
                        }
                        else
                        {
                            if (_activePeers.TryRemove(peerKey, out var removed))
                            {
                                OnPeerLeftCall?.Invoke(removed);
                            }
                        }

                        OnCallStateUpdated?.Invoke(_activePeers.Count);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erro no listen: {ex.Message}");
            }
        }
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, token);

                var now = DateTime.Now;
                var expired = _activePeers
                    .Where(p => (now - p.Value.LastSeen).TotalSeconds > PeerTimeoutSeconds)
                    .Select(p => p.Key)
                    .ToList();

                foreach (var key in expired)
                {
                    if (_activePeers.TryRemove(key, out var expiredPeer))
                    {
                        OnPeerLeftCall?.Invoke(expiredPeer);
                    }
                }

                if (expired.Count > 0)
                {
                    OnCallStateUpdated?.Invoke(_activePeers.Count);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erro no cleanup: {ex.Message}");
            }
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
        _activePeers.Clear();
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
    }
}