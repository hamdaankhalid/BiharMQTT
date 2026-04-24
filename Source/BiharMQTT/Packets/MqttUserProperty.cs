// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;

namespace BiharMQTT.Packets;

public struct MqttUserProperty
{
    readonly ReadOnlyMemory<byte> _valueBuffer;

    [Obsolete("Please use more performance constructor with ArraySegment<byte> or ReadOnlyMemory<byte> for the value.")]
    public MqttUserProperty(string name, string value)
        : this(name, new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(value ?? throw new ArgumentNullException(nameof(value)))))
    {
    }

    public MqttUserProperty(string name, ArraySegment<byte> value)
        : this(name, CreateMemory(value))
    {
    }

    public MqttUserProperty(string name, ReadOnlyMemory<byte> value)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _valueBuffer = value;
    }

    public string Name { get; }

    public ReadOnlyMemory<byte> ValueBuffer => _valueBuffer;

    [Obsolete("Please use more performance property ValueBuffer or the MqttUserPropertyExtensionMethod `ReadValueAsString`")]
    public readonly string Value => this.ReadValueAsString();

    public override readonly bool Equals(object obj)
    {
        if (obj is MqttUserProperty other)
        {
            return Equals(other);
        }

        return false;
    }

    public readonly bool Equals(MqttUserProperty other)
    {
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal))
        {
            return false;
        }

        return _valueBuffer.Span.SequenceEqual(other._valueBuffer.Span);
    }


    public override readonly int GetHashCode()
    {
        var hashCode = new HashCode();

        if (!string.IsNullOrEmpty(Name))
        {
            hashCode.Add(Name, StringComparer.Ordinal);
        }
        else
        {
            hashCode.Add(0);
        }

        hashCode.AddBytes(_valueBuffer.Span);

        return hashCode.ToHashCode();
    }


    public override readonly string ToString()
    {
        return $"{Name} = {this.ReadValueAsString()}";
    }

    static ReadOnlyMemory<byte> CreateMemory(ArraySegment<byte> value)
    {
        if (value.Array == null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        return new ReadOnlyMemory<byte>(value.Array, value.Offset, value.Count);
    }
}