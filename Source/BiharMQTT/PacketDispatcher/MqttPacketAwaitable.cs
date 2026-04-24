// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Adapter;
using BiharMQTT.Exceptions;
using BiharMQTT.Internal;
using BiharMQTT.Protocol;

namespace BiharMQTT.PacketDispatcher;

public sealed class MqttPacketAwaitable : IMqttPacketAwaitable
{
    readonly AsyncTaskCompletionSource<ReceivedMqttPacket> _promise = new();
    readonly MqttPacketDispatcher _owningPacketDispatcher;

    public MqttPacketAwaitable(MqttControlPacketType packetType, ushort packetIdentifier, MqttPacketDispatcher owningPacketDispatcher)
    {
        Filter = new MqttPacketAwaitableFilter
        {
            PacketType = packetType,
            Identifier = packetIdentifier
        };

        _owningPacketDispatcher = owningPacketDispatcher ?? throw new ArgumentNullException(nameof(owningPacketDispatcher));
    }

    public MqttPacketAwaitableFilter Filter { get; }

    public async Task<ReceivedMqttPacket> WaitOneAsync(CancellationToken cancellationToken)
    {
        await using (cancellationToken.Register(() => Fail(new MqttCommunicationTimedOutException())))
        {
            return await _promise.Task.ConfigureAwait(false);
        }
    }

    public void Complete(ReceivedMqttPacket packet)
    {
        _promise.TrySetResult(packet);
    }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        _promise.TrySetException(exception);
    }

    public void Cancel()
    {
        _promise.TrySetCanceled();
    }

    public void Dispose()
    {
        _owningPacketDispatcher.RemoveAwaitable(this);
    }
}