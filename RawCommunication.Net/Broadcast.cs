using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AddressInUseException = System.InvalidOperationException;

namespace RawCommunication.Net
{
    public class Broadcast
    {
        private IPEndPoint _endPoint;
        private readonly ITrace _trace;
        private Socket _listenSocket;
        private Task _listenTask;
        private Exception _listenException;
        private volatile bool _unbinding;
        private readonly SocketAwaitable _receiveFromAwaitable = new SocketAwaitable();
        private readonly SocketAwaitable _sendToAwaitable = new SocketAwaitable();

        public Action<EndPoint, ArraySegment<byte>> ReceivedFrom { get; set; } // Delegate

        public Broadcast(IPEndPoint endPoint, ITrace trace)
        {
            Debug.Assert(endPoint != null);
            Debug.Assert(trace != null);

            _endPoint = endPoint;
            _trace = trace;
        }

        public Task BindAsync()
        {
            if (_listenSocket != null)
            {
                throw new InvalidOperationException("Transport is already bound.");
            }

            IPEndPoint endPoint = _endPoint;

            var listenSocket = new Socket(endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

            // Kestrel expects IPv6Any to bind to both IPv6 and IPv4
            if (endPoint.Address == IPAddress.IPv6Any)
            {
                listenSocket.DualMode = true;
            }

            try
            {
                listenSocket.Bind(endPoint);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw new AddressInUseException(e.Message, e);
            }

            // If requested port was "0", replace with assigned dynamic port.
            if (_endPoint.Port == 0)
            {
                _endPoint = (IPEndPoint)listenSocket.LocalEndPoint;
            }

            _listenSocket = listenSocket;

            _listenTask = Task.Run(RunReceiveFromLoopAsync);

            return Task.CompletedTask;
        }

        public async Task UnbindAsync()
        {
            if (_listenSocket != null)
            {
                _unbinding = true;
                _listenSocket.Dispose();

                Debug.Assert(_listenTask != null);
                await _listenTask.ConfigureAwait(false);

                _unbinding = false;
                _listenSocket = null;
                _listenTask = null;

                if (_listenException != null)
                {
                    var exInfo = ExceptionDispatchInfo.Capture(_listenException);
                    _listenException = null;
                    exInfo.Throw();
                }
            }
        }

        public Task StopAsync()
        {
            _receiveFromAwaitable.Dispose();
            _sendToAwaitable.Dispose();
            return Task.CompletedTask;
        }

        private async Task RunReceiveFromLoopAsync()
        {
            try
            {
                var buffer = new ArraySegment<byte>(new byte[Connection.MinAllocBufferSize]);
                var awaitable = _receiveFromAwaitable;
                awaitable.EventArgs.SetBuffer(buffer.Array, buffer.Offset, buffer.Count);
                awaitable.EventArgs.RemoteEndPoint = _endPoint;
                while (true)
                {
                    try
                    {
                        if (!await _listenSocket.ReceiveFromAsync(awaitable))
                            throw new SocketException((int)awaitable.EventArgs.SocketError);

                        var bytesReceived = awaitable.EventArgs.BytesTransferred;

                        if (bytesReceived == 0)
                        {
                            // FIN
                            _trace.ConnectionReceiveFromFin("(null)");
                            break;
                        }

#if NETCOREAPP2_0
                        ReceivedFrom(awaitable.EventArgs.RemoteEndPoint, buffer.Slice(buffer.Offset, bytesReceived));
#else
                        ReceivedFrom(awaitable.EventArgs.RemoteEndPoint, new ArraySegment<byte>(buffer.Array, buffer.Offset, bytesReceived));
#endif
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        // REVIEW: Should there be a seperate log message for a connection reset this early?
                        _trace.ConnectionReset("(null)");
                    }
                    catch (SocketException ex) when (!_unbinding)
                    {
                        _trace.ConnectionError("(null)", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_unbinding)
                {
                    // Means we must be unbinding. Eat the exception.
                }
                else
                {
                    _trace.LogCritical(ex, $"Unexpected exeption in {nameof(Broadcast)}.{nameof(RunReceiveFromLoopAsync)}.");
                    _listenException = ex;
                }
            }
        }

        public async Task SendToAsync(ArraySegment<byte> buffer)
        {
            using (var socket = new Socket(_endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.EnableBroadcast = true;
                var group = new IPEndPoint(IPAddress.Broadcast, _endPoint.Port);
                var awaitable = _sendToAwaitable;
                awaitable.EventArgs.SetBuffer(buffer.Array, buffer.Offset, buffer.Count);
                awaitable.EventArgs.RemoteEndPoint = group;
                if (!await socket.SendToAsync(awaitable))
                    throw new SocketException((int)awaitable.EventArgs.SocketError);
            }
        }
    }
}