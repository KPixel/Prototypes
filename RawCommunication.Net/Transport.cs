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
    public class Transport
    {
        public IPEndPoint EndPoint { get; private set; }
        private readonly ITrace _trace;
        private Socket _listenSocket;
        private Task _listenTask;
        private Exception _listenException;
        private volatile bool _unbinding;
        private readonly SocketAwaitable _acceptAwaitable = new SocketAwaitable();
        private readonly SocketAwaitable _connectAwaitable = new SocketAwaitable();

        public Action<Connection> ConnectionStarting { get; set; } // Delegate

        public Transport(IPEndPoint endPoint, ITrace trace)
        {
            Debug.Assert(endPoint != null);
            Debug.Assert(trace != null);

            EndPoint = endPoint;
            _trace = trace;
        }

        public Task BindAsync()
        {
            if (_listenSocket != null)
            {
                throw new InvalidOperationException("Transport is already bound.");
            }

            IPEndPoint endPoint = EndPoint;

            var listenSocket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

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
            if (EndPoint.Port == 0)
            {
                EndPoint = (IPEndPoint)listenSocket.LocalEndPoint;
            }

            listenSocket.Listen(512);

            _listenSocket = listenSocket;

            _listenTask = Task.Run(RunAcceptLoopAsync);

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
            _acceptAwaitable.Dispose();
            _connectAwaitable.Dispose();
            return Task.CompletedTask;
        }

        private async Task RunAcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    try
                    {
                        var awaitable = _acceptAwaitable;
                        awaitable.EventArgs.AcceptSocket = null; // Since we are re-using the EventArgs, we must reset it before AcceptAsync()
                        if (!await _listenSocket.AcceptAsync(awaitable))
                            throw new SocketException((int)awaitable.EventArgs.SocketError);

                        Start(awaitable.EventArgs.AcceptSocket);
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
                    _trace.LogCritical(ex, $"Unexpected exeption in {nameof(Transport)}.{nameof(RunAcceptLoopAsync)}.");
                    _listenException = ex;
                }
            }
        }

        public async Task ConnectAsync(IPEndPoint endPoint)
        {
            Debug.Assert(endPoint != null);

            var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            var awaitable = _connectAwaitable;
            awaitable.EventArgs.RemoteEndPoint = endPoint;
            if (!await socket.ConnectAsync(awaitable))
                throw new SocketException((int)awaitable.EventArgs.SocketError);
            Start(socket);
        }

        private void Start(Socket socket)
        {
            var connection = new Connection(socket, _trace);
            ConnectionStarting(connection);
            _ = connection.StartAsync();
        }
    }
}