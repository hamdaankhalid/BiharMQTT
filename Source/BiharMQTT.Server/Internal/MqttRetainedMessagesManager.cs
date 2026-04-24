// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Diagnostics.Logger;
using BiharMQTT.Internal;

namespace BiharMQTT.Server.Internal;

public sealed class MqttRetainedMessagesManager : IDisposable
{
    readonly MqttNetSourceLogger _logger;
    readonly Dictionary<string, MqttApplicationMessage> _messages = new(4096);
    readonly AsyncLock _storageAccessLock = new();

    public MqttRetainedMessagesManager(IMqttNetLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger.WithSource(nameof(MqttRetainedMessagesManager));
    }

    public async Task ClearMessages()
    {
        lock (_messages)
        {
            _messages.Clear();
        }
    }

    public void Dispose()
    {
        _storageAccessLock.Dispose();
    }

    public Task<MqttApplicationMessage> GetMessage(string topic)
    {
        lock (_messages)
        {
            if (_messages.TryGetValue(topic, out var message))
            {
                return Task.FromResult(message);
            }
        }

        return Task.FromResult<MqttApplicationMessage>(null);
    }

    public Task<IList<MqttApplicationMessage>> GetMessages()
    {
        lock (_messages)
        {
            var result = new List<MqttApplicationMessage>(_messages.Values);
            return Task.FromResult((IList<MqttApplicationMessage>)result);
        }
    }

    public async Task Start()
    {
        try
        {
            // Retained message loading event was removed; start with empty store.
            lock (_messages)
            {
                _messages.Clear();
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Unhandled exception while loading retained messages.");
        }
    }

    public async Task UpdateMessage(string clientId, MqttApplicationMessage applicationMessage)
    {
        ArgumentNullException.ThrowIfNull(applicationMessage);

        try
        {
            lock (_messages)
            {
                var payload = applicationMessage.Payload;
                var hasPayload = payload.Length > 0;

                if (!hasPayload)
                {
                    _messages.Remove(applicationMessage.Topic);
                    _logger.Verbose("Client '{0}' cleared retained message for topic '{1}'.", clientId, applicationMessage.Topic);
                }
                else
                {
                    if (!_messages.TryGetValue(applicationMessage.Topic, out var existingMessage))
                    {
                        _messages[applicationMessage.Topic] = applicationMessage;
                    }
                    else
                    {
                        if (existingMessage.QualityOfServiceLevel != applicationMessage.QualityOfServiceLevel || !MqttMemoryHelper.SequenceEqual(existingMessage.Payload, payload))
                        {
                            _messages[applicationMessage.Topic] = applicationMessage;
                        }
                    }

                    _logger.Verbose("Client '{0}' set retained message for topic '{1}'.", clientId, applicationMessage.Topic);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Unhandled exception while handling retained messages.");
        }
    }
}