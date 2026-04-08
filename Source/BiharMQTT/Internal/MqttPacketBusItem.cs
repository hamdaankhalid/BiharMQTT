// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BiharMQTT.Packets;

namespace BiharMQTT.Internal;

public sealed class MqttPacketBusItem
{
    readonly AsyncTaskCompletionSource<MqttPacket> _promise = new();

    int _terminated;

    public MqttPacketBusItem(MqttPacket packet)
    {
        Packet = packet ?? throw new ArgumentNullException(nameof(packet));
    }

    public event EventHandler Completed;

    public MqttPacket Packet { get; }

    /// <summary>
    ///     Optional callback invoked exactly once when this item reaches a terminal state
    ///     (Complete, Cancel, or Fail). Used to release pooled resources such as payload buffers.
    ///     The <see cref="TerminationState" /> value is passed as the argument.
    /// </summary>
    public Action<object> OnTerminated { get; set; }

    /// <summary>
    ///     State object passed to <see cref="OnTerminated" /> when invoked.
    /// </summary>
    public object TerminationState { get; set; }

    public void Cancel()
    {
        _promise.TrySetCanceled();
        InvokeOnTerminated();
    }

    public void Complete()
    {
        _promise.TrySetResult(Packet);
        InvokeOnTerminated();
        Completed?.Invoke(this, EventArgs.Empty);
    }

    public void Fail(Exception exception)
    {
        _promise.TrySetException(exception);
        InvokeOnTerminated();
    }

    public Task<MqttPacket> WaitAsync()
    {
        return _promise.Task;
    }

    void InvokeOnTerminated()
    {
        if (OnTerminated == null)
        {
            return;
        }

        // Ensure exactly-once invocation even if Complete/Cancel/Fail race.
        if (Interlocked.CompareExchange(ref _terminated, 1, 0) == 0)
        {
            OnTerminated.Invoke(TerminationState);
        }
    }
}