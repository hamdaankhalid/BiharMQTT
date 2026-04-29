// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Reflection.Metadata;

namespace BiharMQTT.Server;

public sealed class MqttServerOptions
{
    public TimeSpan DefaultCommunicationTimeout { get; set; } = TimeSpan.FromSeconds(100);

    public MqttServerTcpEndpointOptions DefaultEndpointOptions { get; } = new();

    public bool EnablePersistentSessions { get; set; }

    public MqttServerKeepAliveOptions KeepAliveOptions { get; } = new();

    public int MaxPendingMessagesPerClient { get; set; } = Constants.MaxPendingMessagesPerSession;

    public MqttPendingMessagesOverflowStrategy PendingMessagesOverflowStrategy { get; set; } = MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage;

    public MqttServerTlsTcpEndpointOptions TlsEndpointOptions { get; } = new();

    /// <summary>
    ///     Gets or sets the default and initial size of the packet write buffer.
    ///     It is recommended to set this to a value close to the usual expected packet size * 1.5.
    ///     Do not change this value when no memory issues are experienced.
    /// </summary>
    public int WriterBufferSize { get; set; } = 4096;

    /// <summary>
    ///     Gets or sets the maximum size of the buffer writer. The writer will reduce its internal buffer
    ///     to this value after serializing a packet.
    ///     Do not change this value when no memory issues are experienced.
    /// </summary>
    public int WriterBufferSizeMax { get; set; } = 65535;

    /// <summary>
    ///     Number of dedicated background threads servicing all connected clients' egress traffic.
    ///     N clients are multiplexed onto these M threads via the sender pool's ready queue.
    ///     Default is <see cref="Environment.ProcessorCount"/>. Set higher if egress is dominated by
    ///     blocking writes to slow clients (TLS, congested links).
    /// </summary>
    public int SenderThreadCount { get; set; } = Environment.ProcessorCount;

    /// <summary>
    ///     Optional static-only hook invoked synchronously for every incoming PUBLISH (and every
    ///     server-side <c>MqttServer.Publish</c>) before fan-out. The hook returns a
    ///     <see cref="PublishInterceptResult"/> that decides whether to deliver, silently consume,
    ///     or reject the message.
    ///     <para>
    ///     The function pointer must reference a <c>static</c> method. The
    ///     <see cref="MqttPublishInterceptArgs"/> argument is a <c>ref struct</c> whose buffers are
    ///     valid ONLY for the duration of the call — copy via <c>.ToArray()</c> if you need to
    ///     retain anything.
    ///     </para>
    ///     <para>
    ///     Calling <c>MqttServer.Publish</c> re-entrantly from inside the interceptor is undefined
    ///     behaviour in this version (the per-thread topic scratch buffer would be clobbered).
    ///     </para>
    /// </summary>
    public unsafe delegate*<in MqttPublishInterceptArgs, PublishInterceptResult> PublishInterceptor { get; set; }
}