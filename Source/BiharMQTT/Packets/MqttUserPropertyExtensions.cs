// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;

namespace BiharMQTT.Packets;

public static class MqttUserPropertyExtensions
{
    public static string ReadNameAsString(this MqttUserProperty property)
    {
        var seg = property.Name;
        if (seg.Count == 0) return string.Empty;
        return Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count);
    }

    public static string ReadValueAsString(this MqttUserProperty property)
    {
        var seg = property.Value;
        if (seg.Count == 0) return string.Empty;
        return Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count);
    }
}
