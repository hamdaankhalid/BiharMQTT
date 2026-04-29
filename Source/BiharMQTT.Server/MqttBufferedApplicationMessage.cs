// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using BiharMQTT.Packets;
using BiharMQTT.Protocol;

namespace BiharMQTT.Server;

/// <summary>
///     A value-type application message designed for high-throughput injection scenarios.
///     Unlike <see cref="MqttApplicationMessage" /> (a class), this is a struct that avoids
///     heap allocation. The payload is <see cref="ReadOnlyMemory{T}" /> so callers can use
///     pooled/rented buffers and return them after the inject call completes.
/// </summary>
public readonly struct MqttBufferedApplicationMessage
{
    public string Topic { get; init; }

    /// <summary>
    ///     The message payload backed by a <see cref="ReadOnlyMemory{T}" />.
    ///     Callers may supply memory from <see cref="System.Buffers.ArrayPool{T}" />
    ///     or any other buffer source and reclaim it once
    ///     completes.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    public MqttQualityOfServiceLevel QualityOfServiceLevel { get; init; }

    public bool Retain { get; init; }

    /// <summary>
    ///     MQTT 5 content type.
    /// </summary>
    public string ContentType { get; init; }

    /// <summary>
    ///     MQTT 5 response topic.
    /// </summary>
    public string ResponseTopic { get; init; }

    /// <summary>
    ///     MQTT 5 correlation data.
    /// </summary>
    public byte[] CorrelationData { get; init; }

    /// <summary>
    ///     MQTT 5 message expiry interval in seconds.
    /// </summary>
    public uint MessageExpiryInterval { get; init; }

    /// <summary>
    ///     MQTT 5 payload format indicator.
    /// </summary>
    public MqttPayloadFormatIndicator PayloadFormatIndicator { get; init; }

    /// <summary>
    ///     MQTT 5 user properties.
    /// </summary>
    public List<MqttUserProperty> UserProperties { get; init; }

    /// <summary>
    ///     MQTT 5 subscription identifiers.
    /// </summary>
    public List<uint> SubscriptionIdentifiers { get; init; }
}
