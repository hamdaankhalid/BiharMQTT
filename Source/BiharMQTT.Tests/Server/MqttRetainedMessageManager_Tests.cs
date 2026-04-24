using BiharMQTT.Server.Internal;

namespace BiharMQTT.Tests.Server;

// ReSharper disable InconsistentNaming
[TestClass]
public sealed class MqttRetainedMessageManager_Tests
{
    [TestMethod]
    public async Task MqttRetainedMessageManager_GetUndefinedTopic()
    {
        var logger = new Mockups.TestLogger();
        var retainedMessagesManager = new MqttRetainedMessagesManager(logger);
        var task = retainedMessagesManager.GetMessage("undefined");
        Assert.IsNotNull(task, "Task should not be null");
        var result = await task;
        Assert.IsNull(result, "Null result expected");
    }
}