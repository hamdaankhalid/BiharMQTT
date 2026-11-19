// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BiharMQTT.Packets;

public struct MqttUserProperty
{
    public MqttUserProperty(ArraySegment<byte> name, ArraySegment<byte> value)
    {
        Name = name;
        Value = value;
    }

    public ArraySegment<byte> Name { get; set; }

    public ArraySegment<byte> Value { get; set; }
}