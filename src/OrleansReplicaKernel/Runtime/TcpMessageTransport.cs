using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Serialization;

namespace OrleansReplicaKernel.Runtime;

public sealed class TcpMessageTransport :
    IMessageTransport,
    IBindableMessageTransport,
    IStartableMessageTransport,
    IAsyncDisposable
{
    private readonly string _localNodeName;
    private readonly IPEndPoint _localEndpoint;
    private readonly IReadOnlyDictionary<string, IPEndPoint> _peerEndpoints;
    private readonly IClusterMembershipView _membershipView;
    private readonly BinaryMessageSerializer _messageSerializer;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _heartbeatInterval;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly ConcurrentDictionary<string, OutboundPeerState> _outboundPeers = new(StringComparer.Ordinal);
    private readonly object _dispatcherLock = new();
    private readonly object _lifecycleLock = new();
    private readonly HashSet<TcpTransportConnection> _activeConnections = [];

    private IMessageReceiver? _requestReceiver;
    private IResponseReceiver? _responseReceiver;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private bool _started;

    public TcpMessageTransport(
        string localNodeName,
        IPEndPoint localEndpoint,
        IReadOnlyDictionary<string, IPEndPoint> peerEndpoints,
        IClusterMembershipView membershipView,
        BinaryMessageSerializer messageSerializer,
        TimeSpan heartbeatInterval,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localNodeName);
        ArgumentNullException.ThrowIfNull(localEndpoint);
        ArgumentNullException.ThrowIfNull(peerEndpoints);
        ArgumentNullException.ThrowIfNull(membershipView);
        ArgumentNullException.ThrowIfNull(messageSerializer);

        if (heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), "Heartbeat interval must be positive.");
        }

        _localNodeName = localNodeName;
        _localEndpoint = localEndpoint;
        _peerEndpoints = peerEndpoints;
        _membershipView = membershipView;
        _messageSerializer = messageSerializer;
        _heartbeatInterval = heartbeatInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Bind(IMessageReceiver requestReceiver, IResponseReceiver responseReceiver)
    {
        ArgumentNullException.ThrowIfNull(requestReceiver);
        ArgumentNullException.ThrowIfNull(responseReceiver);

        lock (_dispatcherLock)
        {
            _requestReceiver = requestReceiver;
            _responseReceiver = responseReceiver;
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_started)
            {
                return;
            }

            EnsureBound();

            _listener = new TcpListener(_localEndpoint);
            _listener.Start();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_disposeCancellation.Token));
            _started = true;

            TraceLog.Write(
                "transport",
                $"start tcp listener {_localNodeName} on {_localEndpoint.Address}:{_localEndpoint.Port}");
        }
    }

    public async ValueTask SendAsync(
        InvocationMessage message,
        CancellationToken cancellationToken = default)
    {
        if (_membershipView.GetHealth(message.Target.NodeName) == NodeHealthStatus.Unhealthy)
        {
            throw new RemoteNodeUnavailableException(message.Target.NodeName);
        }

        if (!_peerEndpoints.TryGetValue(message.Target.NodeName, out var endpoint))
        {
            throw new RemoteNodeUnavailableException(message.Target.NodeName);
        }

        TcpTransportConnection connection;
        try
        {
            connection = await GetOrConnectAsync(message.Target.NodeName, endpoint, cancellationToken);
        }
        catch (Exception exception) when (IsNetworkException(exception))
        {
            TraceLog.Write(
                "transport",
                $"tcp connect failed for {message.RequestId:N}/{message.AttemptId:N} to {message.Target.NodeName}: {exception.GetType().Name}");
            throw new RemoteNodeUnavailableException(message.Target.NodeName);
        }

        var payload = _messageSerializer.SerializeInvocationMessage(message);
        try
        {
            TraceLog.Write(
                "transport",
                $"forward request {message.RequestId:N}/{message.AttemptId:N} {_localNodeName} -> {message.Target.NodeName} via tcp");
            await connection.SendFrameAsync(TcpTransportFrameKind.Request, payload, cancellationToken);
        }
        catch (Exception exception) when (IsNetworkException(exception))
        {
            TraceLog.Write(
                "transport",
                $"tcp send failed for {message.RequestId:N}/{message.AttemptId:N} to {message.Target.NodeName}: {exception.GetType().Name}");
            InvalidateOutboundConnection(message.Target.NodeName, connection);
            throw new RemoteNodeUnavailableException(message.Target.NodeName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCancellation.Cancel();

        TcpListener? listener;
        Task? acceptLoop;

        lock (_lifecycleLock)
        {
            listener = _listener;
            acceptLoop = _acceptLoop;
            _listener = null;
            _acceptLoop = null;
            _started = false;
        }

        listener?.Stop();

        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        List<TcpTransportConnection> connections;
        lock (_activeConnections)
        {
            connections = _activeConnections.ToList();
            _activeConnections.Clear();
        }

        foreach (var connection in connections)
        {
            await connection.DisposeAsync();
        }

        foreach (var outboundState in _outboundPeers.Values)
        {
            await outboundState.DisposeAsync();
        }

        _disposeCancellation.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            throw new InvalidOperationException("TCP transport listener has not been started.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => InitializeAcceptedConnectionAsync(client, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                client?.Dispose();
                if (!cancellationToken.IsCancellationRequested)
                {
                    TraceLog.Write("transport", $"tcp accept failed on {_localNodeName}: {exception.GetType().Name}");
                }
            }
        }
    }

    private async Task InitializeAcceptedConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var connection = new TcpTransportConnection(
            this,
            client,
            expectedRemoteNodeName: null,
            onClosed: HandleConnectionClosed);

        try
        {
            await connection.InitializeAcceptedAsync(cancellationToken);
            RegisterActiveConnection(connection);
        }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidOperationException or OperationCanceledException)
        {
            TraceLog.Write("transport", $"reject tcp connection on {_localNodeName}: {exception.GetType().Name}");
            await connection.DisposeAsync();
        }
    }

    private async Task<TcpTransportConnection> GetOrConnectAsync(
        string remoteNodeName,
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        var state = _outboundPeers.GetOrAdd(remoteNodeName, _ => new OutboundPeerState());
        await state.Gate.WaitAsync(cancellationToken);

        try
        {
            if (state.Connection is { IsAlive: true } activeConnection)
            {
                return activeConnection;
            }

            if (state.Connection is not null)
            {
                await state.Connection.DisposeAsync();
                state.Connection = null;
            }

            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(endpoint, cancellationToken);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            var connection = new TcpTransportConnection(
                this,
                client,
                expectedRemoteNodeName: remoteNodeName,
                onClosed: HandleConnectionClosed);
            await connection.InitializeOutgoingAsync(cancellationToken);
            RegisterActiveConnection(connection);
            state.Connection = connection;

            TraceLog.Write(
                "transport",
                $"connect tcp {_localNodeName} -> {remoteNodeName} via {endpoint.Address}:{endpoint.Port}");

            return connection;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private void RegisterActiveConnection(TcpTransportConnection connection)
    {
        lock (_activeConnections)
        {
            _activeConnections.Add(connection);
        }
    }

    private void HandleConnectionClosed(TcpTransportConnection connection)
    {
        lock (_activeConnections)
        {
            _activeConnections.Remove(connection);
        }

        if (connection.RemoteNodeName is { } remoteNodeName)
        {
            InvalidateOutboundConnection(remoteNodeName, connection);
        }
    }

    private void InvalidateOutboundConnection(string remoteNodeName, TcpTransportConnection connection)
    {
        if (_outboundPeers.TryGetValue(remoteNodeName, out var state)
            && ReferenceEquals(state.Connection, connection))
        {
            state.Connection = null;
        }
    }

    private async ValueTask<InvocationResponseMessage> DispatchRequestAsync(
        InvocationMessage message,
        CancellationToken cancellationToken)
    {
        EnsureBound();
        return await _requestReceiver!.ReceiveAsync(message, cancellationToken);
    }

    private ValueTask DispatchResponseAsync(
        InvocationResponseMessage response,
        CancellationToken cancellationToken)
    {
        EnsureBound();
        return _responseReceiver!.ReceiveResponseAsync(response, cancellationToken);
    }

    private void EnsureBound()
    {
        if (_requestReceiver is null || _responseReceiver is null)
        {
            throw new InvalidOperationException("TCP transport has not been bound to request/response receivers.");
        }
    }

    private static bool IsNetworkException(Exception exception)
        => exception is IOException or SocketException or ObjectDisposedException;

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    private const int MaxFrameLength = 16 * 1024 * 1024; // 16 MB

    private async Task<TcpTransportFrame> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, cancellationToken);
        var frameLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);

        if (frameLength <= 0)
        {
            throw new InvalidOperationException($"Invalid TCP transport frame length '{frameLength}'.");
        }

        if (frameLength > MaxFrameLength)
        {
            throw new InvalidOperationException($"TCP transport frame length '{frameLength}' exceeds maximum '{MaxFrameLength}'.");
        }

        var frameBuffer = new byte[frameLength];
        await ReadExactlyAsync(stream, frameBuffer, cancellationToken);

        return new TcpTransportFrame(
            (TcpTransportFrameKind)frameBuffer[0],
            frameLength == 1 ? ReadOnlyMemory<byte>.Empty : frameBuffer.AsMemory(1));
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("TCP transport connection closed unexpectedly.");
            }

            offset += read;
        }
    }

    private async Task WriteFrameAsync(
        NetworkStream stream,
        SemaphoreSlim writeLock,
        TcpTransportFrameKind kind,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        Action onWriteCompleted)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            var header = new byte[5];
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), payload.Length + 1);
            header[4] = (byte)kind;

            await stream.WriteAsync(header, cancellationToken);
            if (!payload.IsEmpty)
            {
                await stream.WriteAsync(payload, cancellationToken);
            }

            await stream.FlushAsync(cancellationToken);
            onWriteCompleted();
        }
        finally
        {
            writeLock.Release();
        }
    }

    private sealed class OutboundPeerState : IAsyncDisposable
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public TcpTransportConnection? Connection { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (Connection is not null)
            {
                await Connection.DisposeAsync();
                Connection = null;
            }

            Gate.Dispose();
        }
    }

    private sealed class TcpTransportConnection : IAsyncDisposable
    {
        private readonly TcpMessageTransport _owner;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly string? _expectedRemoteNodeName;
        private readonly Action<TcpTransportConnection> _onClosed;
        private readonly CancellationTokenSource _connectionCancellation = new();

        private DateTimeOffset _lastReceivedUtc;
        private DateTimeOffset _lastSentUtc;
        private Task? _receiveLoop;
        private Task? _heartbeatLoop;
        private int _disposed;

        public TcpTransportConnection(
            TcpMessageTransport owner,
            TcpClient client,
            string? expectedRemoteNodeName,
            Action<TcpTransportConnection> onClosed)
        {
            _owner = owner;
            _client = client;
            _stream = client.GetStream();
            _expectedRemoteNodeName = expectedRemoteNodeName;
            _onClosed = onClosed;
            _lastReceivedUtc = owner.GetUtcNow();
            _lastSentUtc = owner.GetUtcNow();
        }

        public string? RemoteNodeName { get; private set; }

        public bool IsAlive => Volatile.Read(ref _disposed) == 0;

        public async Task InitializeAcceptedAsync(CancellationToken cancellationToken)
        {
            var hello = await _owner.ReadFrameAsync(_stream, cancellationToken);
            if (hello.Kind != TcpTransportFrameKind.Hello)
            {
                throw new InvalidOperationException($"Expected TCP hello frame but received '{hello.Kind}'.");
            }

            var reader = new BinaryBufferReader(hello.Payload.Span);
            var remoteNodeName = reader.ReadString();
            reader.EnsureFullyConsumed();

            if (_owner._peerEndpoints.Count > 0 && !_owner._peerEndpoints.ContainsKey(remoteNodeName))
            {
                throw new InvalidOperationException($"TCP hello from unexpected node '{remoteNodeName}'.");
            }

            RemoteNodeName = remoteNodeName;
            StartBackgroundLoops();
        }

        public async Task InitializeOutgoingAsync(CancellationToken cancellationToken)
        {
            RemoteNodeName = _expectedRemoteNodeName
                ?? throw new InvalidOperationException("Outgoing TCP transport connection requires a remote node name.");

            var helloWriter = new BinaryBufferWriter();
            helloWriter.WriteString(_owner._localNodeName);
            await SendFrameAsync(TcpTransportFrameKind.Hello, helloWriter.ToArray(), cancellationToken);
            StartBackgroundLoops();
        }

        public async ValueTask SendFrameAsync(
            TcpTransportFrameKind kind,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            try
            {
                await _owner.WriteFrameAsync(
                    _stream,
                    _writeLock,
                    kind,
                    payload,
                    cancellationToken,
                    () => _lastSentUtc = _owner.GetUtcNow());
            }
            catch
            {
                await DisposeCoreAsync(awaitReceiveLoop: false, awaitHeartbeatLoop: false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
            => await DisposeCoreAsync(awaitReceiveLoop: true, awaitHeartbeatLoop: true);

        private async ValueTask DisposeCoreAsync(bool awaitReceiveLoop, bool awaitHeartbeatLoop)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _connectionCancellation.Cancel();
            _client.Dispose();

            if (awaitReceiveLoop && _receiveLoop is not null)
            {
                try
                {
                    await _receiveLoop;
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (awaitHeartbeatLoop && _heartbeatLoop is not null)
            {
                try
                {
                    await _heartbeatLoop;
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            _onClosed(this);
            _writeLock.Dispose();
            _connectionCancellation.Dispose();
        }

        private void StartBackgroundLoops()
        {
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_connectionCancellation.Token), CancellationToken.None);
            _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_connectionCancellation.Token), CancellationToken.None);

            TraceLog.Write(
                "transport",
                $"establish tcp session {_owner._localNodeName} <-> {RemoteNodeName}");
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await _owner.ReadFrameAsync(_stream, cancellationToken);
                    _lastReceivedUtc = _owner.GetUtcNow();

                    switch (frame.Kind)
                    {
                        case TcpTransportFrameKind.Request:
                            _ = Task.Run(() => HandleRequestFrameAsync(frame.Payload, cancellationToken), CancellationToken.None);
                            break;
                        case TcpTransportFrameKind.Response:
                            var response = _owner._messageSerializer.DeserializeInvocationResponse(frame.Payload);
                            await _owner.DispatchResponseAsync(response, cancellationToken);
                            break;
                        case TcpTransportFrameKind.HeartbeatPing:
                            await SendFrameAsync(TcpTransportFrameKind.HeartbeatAck, ReadOnlyMemory<byte>.Empty, cancellationToken);
                            break;
                        case TcpTransportFrameKind.HeartbeatAck:
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported TCP transport frame '{frame.Kind}'.");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    TraceLog.Write(
                        "transport",
                        $"tcp session {_owner._localNodeName} <-> {RemoteNodeName} closed: {exception.GetType().Name}");
                }
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await DisposeCoreAsync(awaitReceiveLoop: false, awaitHeartbeatLoop: true);
                }
            }
        }

        private async Task HandleRequestFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            try
            {
                var request = _owner._messageSerializer.DeserializeInvocationMessage(payload);
                var response = await _owner.DispatchRequestAsync(request, cancellationToken);
                var responsePayload = _owner._messageSerializer.SerializeInvocationResponse(response);
                await SendFrameAsync(TcpTransportFrameKind.Response, responsePayload, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    TraceLog.Write(
                        "transport",
                        $"tcp request handling failed on {_owner._localNodeName} from {RemoteNodeName}: {exception.GetType().Name}");
                }
            }
        }

        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_owner._heartbeatInterval, _owner._timeProvider, cancellationToken);

                    if (_owner.GetUtcNow() - _lastReceivedUtc > _owner._heartbeatInterval * 4)
                    {
                        TraceLog.Write(
                            "transport",
                            $"tcp heartbeat timeout {_owner._localNodeName} <-> {RemoteNodeName}");
                        await DisposeCoreAsync(awaitReceiveLoop: true, awaitHeartbeatLoop: false);
                        return;
                    }

                    await SendFrameAsync(TcpTransportFrameKind.HeartbeatPing, ReadOnlyMemory<byte>.Empty, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    TraceLog.Write(
                        "transport",
                        $"tcp heartbeat failed {_owner._localNodeName} <-> {RemoteNodeName}: {exception.GetType().Name}");
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (!IsAlive)
            {
                throw new ObjectDisposedException(nameof(TcpTransportConnection));
            }
        }
    }

    private readonly record struct TcpTransportFrame(
        TcpTransportFrameKind Kind,
        ReadOnlyMemory<byte> Payload);

    private enum TcpTransportFrameKind : byte
    {
        Hello = 1,
        Request = 2,
        Response = 3,
        HeartbeatPing = 4,
        HeartbeatAck = 5
    }
}
