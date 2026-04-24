// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BiharMQTT.Server.Internal;

public sealed class MqttPacketIdentifierProvider
{
    int _value;

    public ushort GetNextPacketIdentifier()
    {
        var v = Interlocked.Increment(ref _value);
        // Wrap 1..65535; 0 is invalid per MQTT spec
        return (ushort)((v - 1) % 65535 + 1);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _value, 0);
    }
}
