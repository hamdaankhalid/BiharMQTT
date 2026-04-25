using BiharMQTT.Packets;
using static BiharMQTT.Internal.MqttSegmentHelper;

namespace BiharMQTT.Server.EnhancedAuthentication;

public static class ExchangeEnhancedAuthenticationResultFactory
{
    static byte[] SegmentToByteArray(ArraySegment<byte> seg) => seg.Count == 0 ? null : seg.ToArray();

    public static ExchangeEnhancedAuthenticationResult Create(MqttAuthPacket authPacket)
    {
        return new ExchangeEnhancedAuthenticationResult
        {
            AuthenticationData = SegmentToByteArray(authPacket.AuthenticationData),

            ReasonString = SegmentToString(authPacket.ReasonString),
            UserProperties = authPacket.UserProperties
        };
    }

    public static ExchangeEnhancedAuthenticationResult Create(MqttDisconnectPacket disconnectPacket)
    {
        return new ExchangeEnhancedAuthenticationResult
        {
            AuthenticationData = null,
            ReasonString = SegmentToString(disconnectPacket.ReasonString),
            UserProperties = disconnectPacket.UserProperties

            // SessionExpiryInterval makes no sense because the connection is not yet made!
            // ServerReferences makes no sense when the client initiated a DISCONNECT!
        };
    }
}