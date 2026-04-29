// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BiharMQTT.Server;

/// <summary>
/// Decision returned by a publish interceptor. Determines how the broker
/// continues processing the message after the user's static hook runs.
/// </summary>
public enum PublishInterceptResult : byte
{
    /// <summary>
    /// Continue down the normal pipeline: fan out to matching subscribers
    /// and acknowledge with success.
    /// </summary>
    Allow = 0,

    /// <summary>
    /// The broker (or the user's hook) consumed the message; do not fan
    /// out to subscribers. Acknowledge as if delivery succeeded — used for
    /// publisher-to-server-only topics ($SYS/* and similar).
    /// </summary>
    Consume = 1,

    /// <summary>
    /// Reject the message. Skip fan-out. For QoS 1 / QoS 2 the PUBACK /
    /// PUBREC carries <c>NotAuthorized</c>. QoS 0 silently drops.
    /// </summary>
    Reject = 2,
}
