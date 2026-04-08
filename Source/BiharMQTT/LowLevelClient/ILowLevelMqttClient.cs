using BiharMQTT.Diagnostics.PacketInspection;
using BiharMQTT.Packets;

namespace BiharMQTT.LowLevelClient;

public interface ILowLevelMqttClient : IDisposable
{
    event Func<InspectMqttPacketEventArgs, Task> InspectPacketAsync;

    bool IsConnected { get; }

    Task ConnectAsync(MqttClientOptions options, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<MqttPacket> ReceiveAsync(CancellationToken cancellationToken = default);

    Task SendAsync(MqttPacket packet, CancellationToken cancellationToken = default);
}