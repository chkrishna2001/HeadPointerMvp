using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HeadPointerMvp.Domain;
using HeadPointerMvp.Infrastructure;

namespace HeadPointerMvp.Services;

/// <summary>
/// Adapter for a small local bridge that forwards OpenSeeFace pose data as JSON UDP:
/// { "yaw": -15.2, "pitch": 1.3, "roll": 0.4 }. Keeping the bridge external means
/// its Python/native dependencies never run inside the desktop application's process.
/// </summary>
public sealed class OpenSeeFaceHeadPoseSource(string host, int port, FileLogger logger) : IHeadPoseSource
{
    private readonly IPEndPoint _endpoint = CreateLoopbackEndpoint(host, port);
    private readonly FileLogger _logger = logger;
    private UdpClient? _client;
    private CancellationTokenSource? _stop;
    private Task? _receiveTask;

    public event EventHandler<HeadPose>? PoseReceived;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _client = new UdpClient(_endpoint);
        _receiveTask = ReceiveLoopAsync(_stop.Token);
        return _logger.InfoAsync($"OpenSeeFace bridge listening at {_endpoint}.");
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _client is not null)
            {
                var packet = await _client.ReceiveAsync(cancellationToken);
                var pose = JsonSerializer.Deserialize<PosePacket>(packet.Buffer, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (pose is not null)
                {
                    PoseReceived?.Invoke(this, new HeadPose(pose.Yaw, pose.Pitch, pose.Roll, DateTimeOffset.UtcNow));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await _logger.InfoAsync($"Pose receiver stopped: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop is not null) _stop.Cancel();
        _client?.Dispose();
        if (_receiveTask is not null) await _receiveTask;
        _stop?.Dispose();
    }

    private static IPEndPoint CreateLoopbackEndpoint(string host, int port)
    {
        if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("The pose bridge must use a loopback IP address such as 127.0.0.1.", nameof(host));
        return new IPEndPoint(address, port);
    }

    private sealed class PosePacket
    {
        public float Yaw { get; init; }
        public float Pitch { get; init; }
        public float Roll { get; init; }
    }
}
