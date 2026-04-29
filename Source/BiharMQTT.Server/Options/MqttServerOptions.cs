// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Reflection.Metadata;
using BiharMQTT.Protocol;
using BiharMQTT.Formatter;

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

    /// <summary>
    ///     Optional static-only hook invoked synchronously during the CONNECT handshake,
    ///     after the broker's own validation passes. The hook returns the
    ///     <see cref="MqttConnectReasonCode"/> the broker will put on the CONNACK; return
    ///     <see cref="MqttConnectReasonCode.Success"/> to allow, or any failure code
    ///     (e.g. <see cref="MqttConnectReasonCode.NotAuthorized"/>,
    ///     <see cref="MqttConnectReasonCode.BadUserNameOrPassword"/>) to reject before
    ///     the session and connection are installed.
    ///     <para>
    ///     The function pointer must reference a <c>static</c> method. The
    ///     <see cref="MqttConnectionValidatorArgs"/> argument is a <c>ref struct</c> whose
    ///     buffers are valid ONLY for the duration of the call — copy via
    ///     <c>.ToArray()</c> if you need to retain anything (e.g. to log a token).
    ///     </para>
    ///     <para>
    ///     Use <see cref="MqttConnectionValidatorArgs.IsSecureConnection"/> to short-circuit
    ///     validation on already-trusted (mTLS) endpoints — e.g. require a Firebase token
    ///     in <c>Username</c>/<c>Password</c> only when <c>IsSecureConnection == false</c>.
    ///     </para>
    /// </summary>
    public unsafe delegate*<in MqttConnectionValidatorArgs, MqttConnectReasonCode> ConnectionValidator { get; set; }

    /// <summary>
    ///     Optional static-only hook invoked synchronously after a client's CONNACK has been
    ///     sent and its session is installed in the broker. Use for connection-counting,
    ///     audit logging, or session-bootstrap side effects. The hook MUST NOT block — it
    ///     runs on the connection's lifetime task before its receive loop starts.
    ///     <para>
    ///     The function pointer must reference a <c>static</c> method.
    ///     </para>
    /// </summary>
    public unsafe delegate*<in MqttClientConnectedArgs, void> ClientConnectedInterceptor { get; set; }

    /// <summary>
    ///     Optional static-only hook invoked once the per-connection lifetime task ends,
    ///     for any reason (peer close, server disconnect, takeover, error). Pairs 1:1 with
    ///     <see cref="ClientConnectedInterceptor"/>. The hook MUST NOT block — it runs on
    ///     the channel teardown path.
    ///     <para>
    ///     The function pointer must reference a <c>static</c> method.
    ///     </para>
    /// </summary>
    public unsafe delegate*<in MqttClientDisconnectedArgs, void> ClientDisconnectedInterceptor { get; set; }
}