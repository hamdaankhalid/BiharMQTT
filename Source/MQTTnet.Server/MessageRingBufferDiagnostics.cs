// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace MQTTnet.Server;

/// <summary>
///     Diagnostic counters for the ring buffer message store.
/// </summary>
public sealed class MessageRingBufferDiagnostics
{
    /// <summary>
    ///     Total capacity of the ring buffer in bytes.
    /// </summary>
    public int CapacityBytes { get; init; }

    /// <summary>
    ///     Total number of messages ever acquired (written into the ring buffer).
    /// </summary>
    public long TotalAcquired { get; init; }

    /// <summary>
    ///     Total number of messages whose ring buffer space has been fully reclaimed
    ///     (all subscribers finished sending).
    /// </summary>
    public long TotalReleased { get; init; }

    /// <summary>
    ///     Number of messages currently in-flight (acquired but not yet fully released).
    /// </summary>
    public long InFlight => TotalAcquired - TotalReleased;
}
