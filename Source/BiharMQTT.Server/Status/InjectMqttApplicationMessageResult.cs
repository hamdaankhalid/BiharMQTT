// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BiharMQTT.Server;

/// <summary>
///     Minimal replacement for the deleted core type.
///     Contains the result of injecting/enqueuing an application message into a session.
/// </summary>
public sealed class InjectMqttApplicationMessageResult
{
    public ushort PacketIdentifier { get; set; }
}
