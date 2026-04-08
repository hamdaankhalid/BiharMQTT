// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace MQTTnet.Server.Internal;

/// <summary>
///     Lightweight event container for the buffered publish interceptor.
///     Stores <see cref="BufferedPublishHandler" /> delegates and invokes them
///     with a <c>ref</c> to a stack-allocated <see cref="InterceptingPublishBufferedEventArgs" />
///     struct — zero heap allocations per message.
/// </summary>
public sealed class RefBufferedPublishEvent
{
    readonly List<BufferedPublishHandler> _handlers = [];
    BufferedPublishHandler[] _handlersForInvoke = [];

    public bool HasHandlers { get; private set; }

    public void AddHandler(BufferedPublishHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_handlers)
        {
            _handlers.Add(handler);
            HasHandlers = true;
            _handlersForInvoke = [.. _handlers];
        }
    }

    public void RemoveHandler(BufferedPublishHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_handlers)
        {
            _handlers.Remove(handler);
            HasHandlers = _handlers.Count > 0;
            _handlersForInvoke = [.. _handlers];
        }
    }

    public void Invoke(ref InterceptingPublishBufferedEventArgs args)
    {
        var handlers = _handlersForInvoke;
        for (var i = 0; i < handlers.Length; i++)
        {
            handlers[i](ref args);
        }
    }
}
