// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BiharMQTT.Diagnostics;

/// <summary>
/// Process-wide debug switch consulted by hot-path Verbose log sites in the
/// TCP/TLS layer. Off by default so the Verbose calls (and their parameter
/// boxing) are skipped entirely. Flip on from host code to trace connection
/// lifecycle, TLS handshakes, and per-packet receive/send activity.
/// </summary>
public static class BiharMqttDiagnostics
{
    public static bool DebugEnabled { get; set; }
}
