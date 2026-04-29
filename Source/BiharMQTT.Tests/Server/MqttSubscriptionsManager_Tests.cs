// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Text;
using BiharMQTT.Internal;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;
using BiharMQTT.Server;
using BiharMQTT.Server.Internal;
using BiharMQTT.Tests.Mockups;

namespace BiharMQTT.Tests.Server;

// ReSharper disable InconsistentNaming
[TestClass]
public sealed class MqttSubscriptionsManager_Tests : BaseTestClass, IDisposable
{
    MqttClientSubscriptionsManager _subscriptionsManager;

    static ArraySegment<byte> Seg(string s) => new ArraySegment<byte>(Encoding.UTF8.GetBytes(s));

    public void Dispose()
    {
        _subscriptionsManager?.Dispose();
    }

    [TestMethod]
    public async Task MqttSubscriptionsManager_SubscribeAndUnsubscribeSingle()
    {
        var sp = new MqttSubscribePacket
        {
            TopicFilters = [new MqttTopicFilter { Topic = Seg("A/B/C") }]
        };

        _subscriptionsManager.Subscribe(ref sp);

        Assert.IsTrue(CheckSubscriptions("A/B/C", MqttQualityOfServiceLevel.AtMostOnce, "").IsSubscribed);

        var up = new MqttUnsubscribePacket
        {
            TopicFilters = new List<ArraySegment<byte>> { Seg("A/B/C") }
        };
        await _subscriptionsManager.Unsubscribe(up, CancellationToken.None);

        Assert.IsFalse(CheckSubscriptions("A/B/C", MqttQualityOfServiceLevel.AtMostOnce, "").IsSubscribed);
    }

    [TestMethod]
    public void MqttSubscriptionsManager_SubscribeDifferentQoSSuccess()
    {
        var sp = new MqttSubscribePacket
        {
            TopicFilters =
            [
                new MqttTopicFilter { Topic = Seg("A/B/C"), QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce }
            ]
        };

        _subscriptionsManager.Subscribe(ref sp);

        var result = CheckSubscriptions("A/B/C", MqttQualityOfServiceLevel.ExactlyOnce, "");
        Assert.IsTrue(result.IsSubscribed);
        Assert.AreEqual(MqttQualityOfServiceLevel.AtMostOnce, result.QualityOfServiceLevel);
    }

    [TestMethod]
    public void MqttSubscriptionsManager_SubscribeSingleNoSuccess()
    {
        var sp = new MqttSubscribePacket
        {
            TopicFilters = [new MqttTopicFilter { Topic = Seg("A/B/C") }]
        };

        _subscriptionsManager.Subscribe(ref sp);

        Assert.IsFalse(CheckSubscriptions("A/B/X", MqttQualityOfServiceLevel.AtMostOnce, "").IsSubscribed);
    }

    [TestMethod]
    public void MqttSubscriptionsManager_SubscribeSingleSuccess()
    {
        var sp = new MqttSubscribePacket
        {
            TopicFilters = [new MqttTopicFilter { Topic = Seg("A/B/C") }]
        };

        _subscriptionsManager.Subscribe(ref sp);

        var result = CheckSubscriptions("A/B/C", MqttQualityOfServiceLevel.AtMostOnce, "");

        Assert.IsTrue(result.IsSubscribed);
        Assert.AreEqual(MqttQualityOfServiceLevel.AtMostOnce, result.QualityOfServiceLevel);
    }

    [TestMethod]
    public void MqttSubscriptionsManager_SubscribeTwoTimesSuccess()
    {
        var sp = new MqttSubscribePacket
        {
            TopicFilters =
            [
                new MqttTopicFilter { Topic = Seg("#"), QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce },
                new MqttTopicFilter { Topic = Seg("A/B/C"), QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce }
            ]
        };

        _subscriptionsManager.Subscribe(ref sp);

        var result = CheckSubscriptions("A/B/C", MqttQualityOfServiceLevel.ExactlyOnce, "");

        Assert.IsTrue(result.IsSubscribed);
        Assert.AreEqual(MqttQualityOfServiceLevel.AtLeastOnce, result.QualityOfServiceLevel);
    }

    [TestMethod]
    public void MqttSubscriptionsManager_SubscribeWildcard1()
    {
        SubscribeToTopic("house/+/room");
        SubscribeToTopic("house/+/room/+");

        CheckIsSubscribed("house/1/room");
        CheckIsSubscribed("house/1/room/bed");
        CheckIsSubscribed("house/1/room/chair");
        CheckIsSubscribed("house/2/room/bed");
        CheckIsSubscribed("house/2/room/chair");

        CheckIsNotSubscribed("house/1/room/bed/cover");
        CheckIsNotSubscribed("house/1/study/bed");
    }

    [TestMethod]
    public void MqttSubscriptionsManager_SubscribeWildcard2()
    {
        SubscribeToTopic("house/+/room");
        SubscribeToTopic("house/+/room/#");

        CheckIsSubscribed("house/1/room");
        CheckIsSubscribed("house/1/room/bed");
        CheckIsSubscribed("house/2/room");
        CheckIsSubscribed("house/2/room/bed");
        CheckIsSubscribed("house/2/room/bed/cover");

        CheckIsNotSubscribed("house/1/level");
        CheckIsNotSubscribed("house/1/level/door");
    }

    [TestMethod]
    public void MqttSubscriptionsManager_SubscribeWildcard3()
    {
        SubscribeToTopic("house/1/room");
        SubscribeToTopic("house/1/room/+");

        CheckIsSubscribed("house/1/room");
        CheckIsSubscribed("house/1/room/bed");
        CheckIsSubscribed("house/1/room/chair");

        CheckIsNotSubscribed("house/2/room");
        CheckIsNotSubscribed("house/2/room/bed/cover");
    }

    [TestMethod]
    public void MqttSubscriptionsManager_SubscribeWildcard4()
    {
        SubscribeToTopic("house/1/+/+");

        CheckIsSubscribed("house/1/room/bed");
        CheckIsSubscribed("house/1/room/chair");

        CheckIsNotSubscribed("house/1/room");
        CheckIsNotSubscribed("house/1/room/bed/cover");
    }

    [TestMethod]
    public void MqttSubscriptionsManager_SubscribeWildcard5()
    {
        SubscribeToTopic("house/1/+/#");

        CheckIsSubscribed("house/1/room/bed");
        CheckIsSubscribed("house/1/room/chair");
        CheckIsSubscribed("house/1/room/chair/leg");
        CheckIsSubscribed("house/1/level/window");
        CheckIsSubscribed("house/1/level/door");

        CheckIsNotSubscribed("house/2/room/bed");
    }

    [TestInitialize]
    public void TestInitialize()
    {
        var logger = new TestLogger();
        var options = new MqttServerOptions();
        var retainedMessagesManager = new MqttRetainedMessagesManager(logger);
        var hugeNativeMemoryPool = new HugeNativeMemoryPool(new (uint, int)[] { (65536, 16) });
        var clientSessionManager = new MqttClientSessionsManager(options, retainedMessagesManager, logger, hugeNativeMemoryPool);

        var session = new MqttSession(
            new MqttConnectPacket
            {
                ClientId = Seg("")
            },
            new ConcurrentDictionary<object, object>(),
            options,
            retainedMessagesManager,
            clientSessionManager,
            hugeNativeMemoryPool);

        _subscriptionsManager = new MqttClientSubscriptionsManager(session, retainedMessagesManager, clientSessionManager);
    }

    void CheckIsNotSubscribed(string topic)
    {
        var result = CheckSubscriptions(topic, MqttQualityOfServiceLevel.AtMostOnce, "");
        Assert.IsFalse(result.IsSubscribed);
    }

    void CheckIsSubscribed(string topic)
    {
        var result = CheckSubscriptions(topic, MqttQualityOfServiceLevel.AtMostOnce, "");
        Assert.IsTrue(result.IsSubscribed);
        Assert.AreEqual(MqttQualityOfServiceLevel.AtMostOnce, result.QualityOfServiceLevel);
    }

    CheckSubscriptionsResult CheckSubscriptions(string topic, MqttQualityOfServiceLevel applicationMessageQoSLevel, string senderClientId)
    {
        MqttTopicHash.Calculate(topic, out var topicHash, out _, out _);
        return _subscriptionsManager.CheckSubscriptions(topic, topicHash, applicationMessageQoSLevel, senderClientId);
    }

    void SubscribeToTopic(string topic)
    {
        var sp = new MqttSubscribePacket
        {
            TopicFilters =
            [
                new MqttTopicFilter { Topic = Seg(topic), QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce }
            ]
        };

        _subscriptionsManager.Subscribe(ref sp);
    }
}