using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ConnectionResetException = System.IO.IOException;
using ConnectionAbortedException = System.OperationCanceledException;

namespace RawCommunication.Net
{
    public class Connection
    {
        public const int MinAllocBufferSize = 2048;

        private readonly Socket _socket;
        private readonly ITrace _trace;

        private volatile bool _aborted;

        private readonly SocketAwaitable _receiveAwaitable = new SocketAwaitable();
        private readonly SocketAwaitable _sendAwaitable = new SocketAwaitable();

        public string ConnectionId { get; }
        public bool IsConnected => _socket.Connected;


        public Action<Exception, Exception> Completed { get; set; } // Delegates
        public Action<ArraySegment<byte>> Received { get; set; }
        public Func<Task<ArraySegment<byte>>> ReadAsync { get; set; }


        public Connection(Socket socket, ITrace trace)
        {
            Debug.Assert(socket != null);
            Debug.Assert(trace != null);
            _socket = socket;
            _trace = trace;
            ConnectionId = _socket.RemoteEndPoint.ToString();
        }

        public async Task StartAsync()
        {
            Exception receiveError = null;
            Exception sendError = null;
            try
            {
                // Spawn send and receive logic
                var receiveTask = DoReceive();
                var sendTask = DoSend();

                // If the sending task completes then close the receive
                // We don't need to do this in the other direction because the kestrel
                // will trigger the output closing once the input is complete.
                if (await Task.WhenAny(receiveTask, sendTask) == sendTask)
                {
                    // Tell the reader it's being aborted
                    _socket.Dispose();
                }

                // Now wait for both to complete
                receiveError = await receiveTask;
                sendError = await sendTask;

                // Dispose the socket(should noop if already called)
                _socket.Dispose();
                _receiveAwaitable.Dispose();
                _sendAwaitable.Dispose();
            }
            catch (Exception ex)
            {
                _trace.LogError(0, ex, $"Unexpected exception in {nameof(Connection)}.{nameof(StartAsync)}.");
            }
            finally
            {
                // Complete the output after disposing the socket
                Completed(receiveError, sendError);
            }
        }

        private async Task<Exception> DoReceive()
        {
            Exception error = null;

            try
            {
                var buffer = new ArraySegment<byte>(new byte[MinAllocBufferSize]);
                var awaitable = _receiveAwaitable;
                awaitable.EventArgs.SetBuffer(buffer.Array, buffer.Offset, buffer.Count);
                while (true)
                {
                    if (!await _socket.ReceiveAsync(awaitable))
                        throw new SocketException((int)awaitable.EventArgs.SocketError);

                    var bytesReceived = awaitable.EventArgs.BytesTransferred;

                    if (bytesReceived == 0)
                    {
                        // FIN
                        _trace.ConnectionReadFin(ConnectionId);
                        break;
                    }

#if NETCOREAPP2_0
                    Received(buffer.Slice(buffer.Offset, bytesReceived));
#else
                    Received(new ArraySegment<byte>(buffer.Array, buffer.Offset, bytesReceived));
#endif
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                error = new ConnectionResetException(ex.Message, ex);
                _trace.ConnectionReset(ConnectionId);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted ||
                                             ex.SocketErrorCode == SocketError.ConnectionAborted ||
                                             ex.SocketErrorCode == SocketError.Interrupted ||
                                             ex.SocketErrorCode == SocketError.InvalidArgument)
            {
                if (!_aborted)
                {
                    // Calling Dispose after ReceiveAsync can cause an "InvalidArgument" error on *nix.
                    error = new ConnectionAbortedException();
                    _trace.ConnectionError(ConnectionId, error);
                }
            }
            catch (ObjectDisposedException)
            {
                if (!_aborted)
                {
                    error = new ConnectionAbortedException();
                    _trace.ConnectionError(ConnectionId, error);
                }
            }
            catch (IOException ex)
            {
                error = ex;
                _trace.ConnectionError(ConnectionId, error);
            }
            catch (Exception ex)
            {
                error = new IOException(ex.Message, ex);
                _trace.ConnectionError(ConnectionId, error);
            }
            finally
            {
                if (_aborted)
                {
                    error = error ?? new ConnectionAbortedException();
                }
            }

            return error;
        }

        private async Task<Exception> DoSend()
        {
            Exception error = null;

            try
            {
                var awaitable = _sendAwaitable;
                while (true)
                {
                    // Wait for data to write
                    var result = await ReadAsync();

                    if (result.Count > 0)
                    {
                        awaitable.EventArgs.SetBuffer(result.Array, result.Offset, result.Count);
                        if (!await _socket.SendAsync(awaitable))
                            throw new SocketException((int)awaitable.EventArgs.SocketError);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                error = null;
            }
            catch (ObjectDisposedException)
            {
                error = null;
            }
            catch (IOException ex)
            {
                error = ex;
            }
            catch (Exception ex)
            {
                error = new IOException(ex.Message, ex);
            }
            finally
            {
                // Make sure to close the connection only after the _aborted flag is set.
                // Without this, the RequestsCanBeAbortedMidRead test will sometimes fail when
                // a BadHttpRequestException is thrown instead of a TaskCanceledException.
                _aborted = true;
                _trace.ConnectionWriteFin(ConnectionId);
                _socket.Shutdown(SocketShutdown.Both);
            }

            return error;
        }
    }
}