// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;

namespace BiharMQTT.Internal;

public static class MqttSegmentHelper
{
    public static string SegmentToString(ArraySegment<byte> segment) =>
        segment.Count == 0 ? string.Empty : Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);
}
