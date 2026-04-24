// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BiharMQTT.Formatter;

public readonly struct MqttFixedHeader
{
    public byte Flags { get; }

    public ushort RemainingLength { get; }

    public ushort TotalLength { get; }

    // For all intents and purposes of MQTT packets being sent WHO TF is sending packets larger than 64KB? 
    // If that ever becomes a thing, we can always change this to an int and update the code accordingly.
    // Like fuck off don't make this shit up.
    public MqttFixedHeader(byte flags, ushort remainingLength, ushort totalLength)
    {
        Flags = flags;
        RemainingLength = remainingLength;
        TotalLength = totalLength;
    }

}