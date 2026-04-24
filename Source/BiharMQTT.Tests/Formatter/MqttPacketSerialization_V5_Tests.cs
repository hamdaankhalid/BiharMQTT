// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Text;
using BiharMQTT.Formatter;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Tests.Formatter;

// ReSharper disable InconsistentNaming
[TestClass]
public sealed class MqttPacketSerialization_V5_Tests
{
    static ArraySegment<byte> Seg(string s) => new ArraySegment<byte>(Encoding.UTF8.GetBytes(s));

    static void AssertSegEqual(ArraySegment<byte> expected, ArraySegment<byte> actual, string message = null)
    {
        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray(), message);
    }

    [TestMethod]
    public void Empty_Auth_Packet_Is_Success()
    {
        var buffer = MqttPacketSerializationHelper.EncodePacket(new MqttAuthPacket(), MqttProtocolVersion.V500);
        var packet = MqttPacketSerializationHelper.DecodePacket(buffer, MqttProtocolVersion.V500);

        Assert.IsNotNull(packet);
        Assert.IsInstanceOfType<MqttAuthPacket>(packet);
    }

    [TestMethod]
    public void Serialize_Full_MqttAuthPacket_V500()
    {
        var authPacket = new MqttAuthPacket
        {
            AuthenticationData = "AuthenticationData"u8.ToArray(),
            AuthenticationMethod = Seg("AuthenticationMethod"),
            ReasonCode = MqttAuthenticateReasonCode.ContinueAuthentication,
            ReasonString = Seg("ReasonString"),
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(authPacket, MqttProtocolVersion.V500);

        CollectionAssert.AreEqual(authPacket.AuthenticationData.ToArray(), deserialized.AuthenticationData.ToArray());
        AssertSegEqual(authPacket.AuthenticationMethod, deserialized.AuthenticationMethod);
        Assert.AreEqual(authPacket.ReasonCode, deserialized.ReasonCode);
        AssertSegEqual(authPacket.ReasonString, deserialized.ReasonString);
        Assert.HasCount(authPacket.UserProperties.Count, deserialized.UserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttConnAckPacket_V500()
    {
        var connAckPacket = new MqttConnAckPacket
        {
            AuthenticationData = "AuthenticationData"u8.ToArray(),
            AuthenticationMethod = Seg("AuthenticationMethod"),
            ReasonCode = MqttConnectReasonCode.ServerUnavailable,
            ReasonString = Seg("ReasonString"),
            ReceiveMaximum = 123,
            ResponseInformation = Seg("ResponseInformation"),
            RetainAvailable = true,
            ServerReference = Seg("ServerReference"),
            AssignedClientIdentifier = Seg("AssignedClientIdentifier"),
            IsSessionPresent = true,
            MaximumPacketSize = 456,
            MaximumQoS = MqttQualityOfServiceLevel.ExactlyOnce,
            ServerKeepAlive = 789,
            SessionExpiryInterval = 852,
            SharedSubscriptionAvailable = true,
            SubscriptionIdentifiersAvailable = true,
            TopicAliasMaximum = 963,
            WildcardSubscriptionAvailable = true,
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(connAckPacket, MqttProtocolVersion.V500);

        CollectionAssert.AreEqual(connAckPacket.AuthenticationData.ToArray(), deserialized.AuthenticationData.ToArray());
        AssertSegEqual(connAckPacket.AuthenticationMethod, deserialized.AuthenticationMethod);
        Assert.AreEqual(connAckPacket.ReasonCode, deserialized.ReasonCode);
        AssertSegEqual(connAckPacket.ReasonString, deserialized.ReasonString);
        Assert.AreEqual(connAckPacket.ReceiveMaximum, deserialized.ReceiveMaximum);
        AssertSegEqual(connAckPacket.ResponseInformation, deserialized.ResponseInformation);
        Assert.AreEqual(connAckPacket.RetainAvailable, deserialized.RetainAvailable);
        AssertSegEqual(connAckPacket.ServerReference, deserialized.ServerReference);
        AssertSegEqual(connAckPacket.AssignedClientIdentifier, deserialized.AssignedClientIdentifier);
        Assert.AreEqual(connAckPacket.IsSessionPresent, deserialized.IsSessionPresent);
        Assert.AreEqual(connAckPacket.MaximumPacketSize, deserialized.MaximumPacketSize);
        Assert.AreEqual(connAckPacket.MaximumQoS, deserialized.MaximumQoS);
        Assert.AreEqual(connAckPacket.ServerKeepAlive, deserialized.ServerKeepAlive);
        Assert.AreEqual(connAckPacket.SessionExpiryInterval, deserialized.SessionExpiryInterval);
        Assert.AreEqual(connAckPacket.SharedSubscriptionAvailable, deserialized.SharedSubscriptionAvailable);
        Assert.AreEqual(connAckPacket.SubscriptionIdentifiersAvailable, deserialized.SubscriptionIdentifiersAvailable);
        Assert.AreEqual(connAckPacket.TopicAliasMaximum, deserialized.TopicAliasMaximum);
        Assert.AreEqual(connAckPacket.WildcardSubscriptionAvailable, deserialized.WildcardSubscriptionAvailable);
        Assert.HasCount(connAckPacket.UserProperties.Count, deserialized.UserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttConnectPacket_V500()
    {
        var connectPacket = new MqttConnectPacket
        {
            Username = Seg("Username"),
            Password = "Password"u8.ToArray(),
            ClientId = Seg("ClientId"),
            AuthenticationData = "AuthenticationData"u8.ToArray(),
            AuthenticationMethod = Seg("AuthenticationMethod"),
            CleanSession = true,
            ReceiveMaximum = 123,
            WillFlag = true,
            WillTopic = Seg("WillTopic"),
            WillMessage = "WillMessage"u8.ToArray(),
            WillRetain = true,
            KeepAlivePeriod = 456,
            MaximumPacketSize = 789,
            RequestProblemInformation = true,
            RequestResponseInformation = true,
            SessionExpiryInterval = 27,
            TopicAliasMaximum = 67,
            WillContentType = Seg("WillContentType"),
            WillCorrelationData = "WillCorrelationData"u8.ToArray(),
            WillDelayInterval = 782,
            WillQoS = MqttQualityOfServiceLevel.ExactlyOnce,
            WillResponseTopic = Seg("WillResponseTopic"),
            WillMessageExpiryInterval = 542,
            WillPayloadFormatIndicator = MqttPayloadFormatIndicator.CharacterData,
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))],
            WillUserProperties = [new MqttUserProperty(Seg("WillFoo"), Encoding.UTF8.GetBytes("WillBar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(connectPacket, MqttProtocolVersion.V500);

        AssertSegEqual(connectPacket.Username, deserialized.Username);
        CollectionAssert.AreEqual(connectPacket.Password.ToArray(), deserialized.Password.ToArray());
        AssertSegEqual(connectPacket.ClientId, deserialized.ClientId);
        CollectionAssert.AreEqual(connectPacket.AuthenticationData.ToArray(), deserialized.AuthenticationData.ToArray());
        AssertSegEqual(connectPacket.AuthenticationMethod, deserialized.AuthenticationMethod);
        Assert.AreEqual(connectPacket.CleanSession, deserialized.CleanSession);
        Assert.AreEqual(connectPacket.ReceiveMaximum, deserialized.ReceiveMaximum);
        Assert.AreEqual(connectPacket.WillFlag, deserialized.WillFlag);
        AssertSegEqual(connectPacket.WillTopic, deserialized.WillTopic);
        CollectionAssert.AreEqual(connectPacket.WillMessage.ToArray(), deserialized.WillMessage.ToArray());
        Assert.AreEqual(connectPacket.WillRetain, deserialized.WillRetain);
        Assert.AreEqual(connectPacket.KeepAlivePeriod, deserialized.KeepAlivePeriod);
        Assert.AreEqual(connectPacket.MaximumPacketSize, deserialized.MaximumPacketSize);
        Assert.AreEqual(connectPacket.RequestProblemInformation, deserialized.RequestProblemInformation);
        Assert.AreEqual(connectPacket.RequestResponseInformation, deserialized.RequestResponseInformation);
        Assert.AreEqual(connectPacket.SessionExpiryInterval, deserialized.SessionExpiryInterval);
        Assert.AreEqual(connectPacket.TopicAliasMaximum, deserialized.TopicAliasMaximum);
        AssertSegEqual(connectPacket.WillContentType, deserialized.WillContentType);
        CollectionAssert.AreEqual(connectPacket.WillCorrelationData.ToArray(), deserialized.WillCorrelationData.ToArray());
        Assert.AreEqual(connectPacket.WillDelayInterval, deserialized.WillDelayInterval);
        Assert.AreEqual(connectPacket.WillQoS, deserialized.WillQoS);
        AssertSegEqual(connectPacket.WillResponseTopic, deserialized.WillResponseTopic);
        Assert.AreEqual(connectPacket.WillMessageExpiryInterval, deserialized.WillMessageExpiryInterval);
        Assert.AreEqual(connectPacket.WillPayloadFormatIndicator, deserialized.WillPayloadFormatIndicator);
        Assert.HasCount(connectPacket.UserProperties.Count, deserialized.UserProperties);
        Assert.HasCount(connectPacket.WillUserProperties.Count, deserialized.WillUserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttDisconnectPacket_V500()
    {
        var disconnectPacket = new MqttDisconnectPacket
        {
            ReasonCode = MqttDisconnectReasonCode.QuotaExceeded,
            ReasonString = Seg("ReasonString"),
            ServerReference = Seg("ServerReference"),
            SessionExpiryInterval = 234,
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(disconnectPacket, MqttProtocolVersion.V500);

        Assert.AreEqual(disconnectPacket.ReasonCode, deserialized.ReasonCode);
        AssertSegEqual(disconnectPacket.ReasonString, deserialized.ReasonString);
        AssertSegEqual(disconnectPacket.ServerReference, deserialized.ServerReference);
        Assert.AreEqual(disconnectPacket.SessionExpiryInterval, deserialized.SessionExpiryInterval);
        Assert.HasCount(disconnectPacket.UserProperties.Count, deserialized.UserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttPingReqPacket_V500()
    {
        var pingReqPacket = new MqttPingReqPacket();

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(pingReqPacket, MqttProtocolVersion.V500);

        Assert.AreEqual("PingReq", deserialized.ToString());
    }

    [TestMethod]
    public void Serialize_Full_MqttPingRespPacket_V500()
    {
        var pingRespPacket = new MqttPingRespPacket();

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(pingRespPacket, MqttProtocolVersion.V500);

        Assert.AreEqual("PingResp", deserialized.ToString());
    }

    [TestMethod]
    public void Serialize_Full_MqttPubAckPacket_V500()
    {
        var pubAckPacket = new MqttPubAckPacket
        {
            PacketIdentifier = 123,
            ReasonCode = MqttPubAckReasonCode.NoMatchingSubscribers,
            ReasonString = Seg("ReasonString"),
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(pubAckPacket, MqttProtocolVersion.V500);

        Assert.AreEqual(pubAckPacket.PacketIdentifier, deserialized.PacketIdentifier);
        Assert.AreEqual(pubAckPacket.ReasonCode, deserialized.ReasonCode);
        AssertSegEqual(pubAckPacket.ReasonString, deserialized.ReasonString);
        Assert.HasCount(pubAckPacket.UserProperties.Count, deserialized.UserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttPubCompPacket_V500()
    {
        var pubCompPacket = new MqttPubCompPacket
        {
            PacketIdentifier = 123,
            ReasonCode = MqttPubCompReasonCode.PacketIdentifierNotFound,
            ReasonString = Seg("ReasonString"),
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(pubCompPacket, MqttProtocolVersion.V500);

        Assert.AreEqual(pubCompPacket.PacketIdentifier, deserialized.PacketIdentifier);
        Assert.AreEqual(pubCompPacket.ReasonCode, deserialized.ReasonCode);
        AssertSegEqual(pubCompPacket.ReasonString, deserialized.ReasonString);
        Assert.HasCount(pubCompPacket.UserProperties.Count, deserialized.UserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttPublishPacket_V500()
    {
        var publishPacket = new MqttPublishPacket
        {
            PacketIdentifier = 123,
            Dup = true,
            Retain = true,
            PayloadSegment = new ArraySegment<byte>("Payload"u8.ToArray()),
            QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce,
            Topic = Seg("Topic"),
            ResponseTopic = Seg("/Response"),
            ContentType = Seg("Content-Type"),
            CorrelationData = "CorrelationData"u8.ToArray(),
            TopicAlias = 27,
            SubscriptionIdentifiers = [123],
            MessageExpiryInterval = 38,
            PayloadFormatIndicator = MqttPayloadFormatIndicator.CharacterData,
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(publishPacket, MqttProtocolVersion.V500);

        Assert.AreEqual(publishPacket.PacketIdentifier, deserialized.PacketIdentifier);
        Assert.AreEqual(publishPacket.Dup, deserialized.Dup);
        Assert.AreEqual(publishPacket.Retain, deserialized.Retain);
        CollectionAssert.AreEqual(publishPacket.Payload.ToArray(), deserialized.Payload.ToArray());
        Assert.AreEqual(publishPacket.QualityOfServiceLevel, deserialized.QualityOfServiceLevel);
        AssertSegEqual(publishPacket.Topic, deserialized.Topic);
        AssertSegEqual(publishPacket.ResponseTopic, deserialized.ResponseTopic);
        AssertSegEqual(publishPacket.ContentType, deserialized.ContentType);
        CollectionAssert.AreEqual(publishPacket.CorrelationData.ToArray(), deserialized.CorrelationData.ToArray());
        Assert.AreEqual(publishPacket.TopicAlias, deserialized.TopicAlias);
        CollectionAssert.AreEqual(publishPacket.SubscriptionIdentifiers, deserialized.SubscriptionIdentifiers);
        Assert.AreEqual(publishPacket.MessageExpiryInterval, deserialized.MessageExpiryInterval);
        Assert.AreEqual(publishPacket.PayloadFormatIndicator, deserialized.PayloadFormatIndicator);
        Assert.HasCount(publishPacket.UserProperties.Count, deserialized.UserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttPubRecPacket_V500()
    {
        var pubRecPacket = new MqttPubRecPacket
        {
            PacketIdentifier = 123,
            ReasonCode = MqttPubRecReasonCode.UnspecifiedError,
            ReasonString = Seg("ReasonString"),
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(pubRecPacket, MqttProtocolVersion.V500);

        Assert.AreEqual(pubRecPacket.PacketIdentifier, deserialized.PacketIdentifier);
        Assert.AreEqual(pubRecPacket.ReasonCode, deserialized.ReasonCode);
        AssertSegEqual(pubRecPacket.ReasonString, deserialized.ReasonString);
        Assert.HasCount(pubRecPacket.UserProperties.Count, deserialized.UserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttPubRelPacket_V500()
    {
        var pubRelPacket = new MqttPubRelPacket
        {
            PacketIdentifier = 123,
            ReasonCode = MqttPubRelReasonCode.PacketIdentifierNotFound,
            ReasonString = Seg("ReasonString"),
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(pubRelPacket, MqttProtocolVersion.V500);

        Assert.AreEqual(pubRelPacket.PacketIdentifier, deserialized.PacketIdentifier);
        Assert.AreEqual(pubRelPacket.ReasonCode, deserialized.ReasonCode);
        AssertSegEqual(pubRelPacket.ReasonString, deserialized.ReasonString);
        Assert.HasCount(pubRelPacket.UserProperties.Count, deserialized.UserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttSubAckPacket_V500()
    {
        var subAckPacket = new MqttSubAckPacket
        {
            PacketIdentifier = 123,
            ReasonString = Seg("ReasonString"),
            ReasonCodes = [MqttSubscribeReasonCode.GrantedQoS1],
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(subAckPacket, MqttProtocolVersion.V500);

        Assert.AreEqual(subAckPacket.PacketIdentifier, deserialized.PacketIdentifier);
        AssertSegEqual(subAckPacket.ReasonString, deserialized.ReasonString);
        Assert.HasCount(subAckPacket.ReasonCodes.Count, deserialized.ReasonCodes);
        Assert.AreEqual(subAckPacket.ReasonCodes[0], deserialized.ReasonCodes[0]);
        Assert.HasCount(subAckPacket.UserProperties.Count, deserialized.UserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttSubscribePacket_V500()
    {
        var subscribePacket = new MqttSubscribePacket
        {
            PacketIdentifier = 123,
            SubscriptionIdentifier = 456,
            TopicFilters =
            [
                new MqttTopicFilter
                {
                    Topic = Seg("Topic"),
                    NoLocal = true,
                    RetainHandling = MqttRetainHandling.SendAtSubscribeIfNewSubscriptionOnly,
                    RetainAsPublished = true,
                    QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce
                }
            ],
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(subscribePacket, MqttProtocolVersion.V500);

        Assert.AreEqual(subscribePacket.PacketIdentifier, deserialized.PacketIdentifier);
        Assert.AreEqual(subscribePacket.SubscriptionIdentifier, deserialized.SubscriptionIdentifier);
        Assert.HasCount(1, deserialized.TopicFilters);
        AssertSegEqual(subscribePacket.TopicFilters[0].Topic, deserialized.TopicFilters[0].Topic);
        Assert.AreEqual(subscribePacket.TopicFilters[0].NoLocal, deserialized.TopicFilters[0].NoLocal);
        Assert.AreEqual(subscribePacket.TopicFilters[0].RetainHandling, deserialized.TopicFilters[0].RetainHandling);
        Assert.AreEqual(subscribePacket.TopicFilters[0].RetainAsPublished, deserialized.TopicFilters[0].RetainAsPublished);
        Assert.AreEqual(subscribePacket.TopicFilters[0].QualityOfServiceLevel, deserialized.TopicFilters[0].QualityOfServiceLevel);
        Assert.HasCount(subscribePacket.UserProperties.Count, deserialized.UserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttUnsubAckPacket_V500()
    {
        var unsubAckPacket = new MqttUnsubAckPacket
        {
            PacketIdentifier = 123,
            ReasonCodes = [MqttUnsubscribeReasonCode.ImplementationSpecificError],
            ReasonString = Seg("ReasonString"),
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(unsubAckPacket, MqttProtocolVersion.V500);

        Assert.AreEqual(unsubAckPacket.PacketIdentifier, deserialized.PacketIdentifier);
        AssertSegEqual(unsubAckPacket.ReasonString, deserialized.ReasonString);
        Assert.HasCount(unsubAckPacket.ReasonCodes.Count, deserialized.ReasonCodes);
        Assert.AreEqual(unsubAckPacket.ReasonCodes[0], deserialized.ReasonCodes[0]);
        Assert.HasCount(unsubAckPacket.UserProperties.Count, deserialized.UserProperties);
    }

    [TestMethod]
    public void Serialize_Full_MqttUnsubscribePacket_V500()
    {
        var unsubscribePacket = new MqttUnsubscribePacket
        {
            PacketIdentifier = 123,
            TopicFilters = [Seg("TopicFilter1")],
            UserProperties = [new MqttUserProperty(Seg("Foo"), Encoding.UTF8.GetBytes("Bar"))]
        };

        var deserialized = MqttPacketSerializationHelper.EncodeAndDecodePacket(unsubscribePacket, MqttProtocolVersion.V500);

        Assert.AreEqual(unsubscribePacket.PacketIdentifier, deserialized.PacketIdentifier);
        Assert.HasCount(unsubscribePacket.TopicFilters.Count, deserialized.TopicFilters);
        AssertSegEqual(unsubscribePacket.TopicFilters[0], deserialized.TopicFilters[0]);
        Assert.HasCount(unsubscribePacket.UserProperties.Count, deserialized.UserProperties);
    }
}