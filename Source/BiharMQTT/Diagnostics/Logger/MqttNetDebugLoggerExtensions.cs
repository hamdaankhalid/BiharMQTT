// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;

namespace BiharMQTT.Diagnostics.Logger;

/// <summary>
/// Hot-path debug logging gated on <see cref="BiharMqttDiagnostics.DebugEnabled"/>.
/// When the flag is off (the default), each call returns after one branch and
/// no parameter array is ever allocated. Underlying transport is a normal
/// Verbose-level publish — the host's <see cref="IMqttNetLogger"/> chooses how
/// to surface it.
/// </summary>
public static class MqttNetDebugLoggerExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ShouldEmit(MqttNetSourceLogger logger) =>
        BiharMqttDiagnostics.DebugEnabled && logger.IsEnabled;

    public static void Debug(this MqttNetSourceLogger logger, string message)
    {
        if (!ShouldEmit(logger)) return;
        logger.Publish(MqttNetLogLevel.Verbose, message, null, null);
    }

    public static void Debug<T1>(this MqttNetSourceLogger logger, string message, T1 p1)
    {
        if (!ShouldEmit(logger)) return;
        logger.Publish(MqttNetLogLevel.Verbose, message, [p1], null);
    }

    public static void Debug<T1, T2>(this MqttNetSourceLogger logger, string message, T1 p1, T2 p2)
    {
        if (!ShouldEmit(logger)) return;
        logger.Publish(MqttNetLogLevel.Verbose, message, [p1, p2], null);
    }

    public static void Debug<T1, T2, T3>(this MqttNetSourceLogger logger, string message, T1 p1, T2 p2, T3 p3)
    {
        if (!ShouldEmit(logger)) return;
        logger.Publish(MqttNetLogLevel.Verbose, message, [p1, p2, p3], null);
    }

    public static void Debug<T1, T2, T3, T4>(this MqttNetSourceLogger logger, string message, T1 p1, T2 p2, T3 p3, T4 p4)
    {
        if (!ShouldEmit(logger)) return;
        logger.Publish(MqttNetLogLevel.Verbose, message, [p1, p2, p3, p4], null);
    }

    public static void Debug<T1, T2, T3, T4, T5>(this MqttNetSourceLogger logger, string message, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5)
    {
        if (!ShouldEmit(logger)) return;
        logger.Publish(MqttNetLogLevel.Verbose, message, [p1, p2, p3, p4, p5], null);
    }

    public static void Debug<T1, T2, T3, T4, T5, T6>(this MqttNetSourceLogger logger, string message, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6)
    {
        if (!ShouldEmit(logger)) return;
        logger.Publish(MqttNetLogLevel.Verbose, message, [p1, p2, p3, p4, p5, p6], null);
    }
}
