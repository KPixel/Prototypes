using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ConnectionResetException = System.IO.IOException;
using ConnectionAbortedException = System.OperationCanceledException;

namespace RawPipelines.Net
{
    internal static class BufferExtensions
    {
        public static ArraySegment<byte> GetArray(this Memory<byte> memory)
        {
            return ((ReadOnlyMemory<byte>)memory).GetArray();
        }

        public static ArraySegment<byte> GetArray(this ReadOnlyMemory<byte> memory)
        {
            if (!MemoryMarshal.TryGetArray(memory, out var result))
            {
                throw new InvalidOperationException("Buffer backed by array was expected");
            }
            return result;
        }
    }

    public abstract class ConnectionContext
    {
        public abstract string ConnectionId { get; }
        public IDuplexPipe Transport { get; }
        private IDuplexPipe Application { get; }

        protected PipeWriter Input => Application.Output;
        protected PipeReader Output => Application.Input;

        protected ConnectionContext()
        {
            var pair = DuplexPipe.CreateConnectionPair(PipeOptions.Default, PipeOptions.Default);
            Transport = pair.Transport;
            Application = pair.Application;
        }
    }

    public class Connection : ConnectionContext // Based on KestrelHttpServer\src\Kestrel.Transport.Sockets\Internal\SocketConnection.cs
    {
        public const int MinAllocBufferSize = 2048;

        private readonly Socket _socket;
        private readonly ITrace _trace;

        private volatile bool _aborted;

        private readonly SocketAwaitable _receiveAwaitable = new SocketAwaitable();
        private readonly SocketAwaitable _sendAwaitable = new SocketAwaitable();

        public override string ConnectionId { get; }

        public Exception ReceiveError { get; private set; }
        public Exception SendError { get; private set; }

        public Connection(Socket socket, ITrace trace)
        {
            Debug.Assert(socket != null);
            Debug.Assert(trace != null);
            _socket = socket;
            _trace = trace;
            ConnectionId = _socket.RemoteEndPoint.ToString();
        }

        public async Task RunAsync()
        {
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
                await receiveTask;
                SendError = await sendTask;

                // Dispose the socket(should noop if already called)
                _socket.Dispose();
                _receiveAwaitable.Dispose();
                _sendAwaitable.Dispose();
            }
            catch (Exception ex)
            {
                _trace.LogError(0, ex, $"Unexpected exception in {nameof(Connection)}.{nameof(RunAsync)}.");
            }
            finally
            {
                // Complete the output after disposing the socket
                Output.Complete();
            }
        }

        private async Task DoReceive()
        {
            Exception error = null;

            try
            {
                var socketError = await ProcessReceives();

                var ex = new SocketException((int) socketError);
                if (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    error = new ConnectionResetException(ex.Message, ex);
                    _trace.ConnectionReset(ConnectionId);
                }
                else if (ex.SocketErrorCode == SocketError.OperationAborted ||
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
                else if (socketError != SocketError.Success)
                {
                    error = ex;
                    _trace.ConnectionError(ConnectionId, error);
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

                ReceiveError = error;
                Input.Complete();
            }
        }

        private async Task<SocketError> ProcessReceives()
        {
            var awaitable = _receiveAwaitable;
            while (true)
            {
                // Ensure we have some reasonable amount of buffer space
                var buffer = Input.GetMemory(MinAllocBufferSize);

#if NETCOREAPP2_1
                awaitable.EventArgs.SetBuffer(buffer);
#else
                var segment = buffer.GetArray();

                awaitable.EventArgs.SetBuffer(segment.Array, segment.Offset, segment.Count);
#endif

                if (!await _socket.ReceiveAsync(awaitable))
                {
                    break;
                }

                var bytesReceived = awaitable.EventArgs.BytesTransferred;

                if (bytesReceived == 0)
                {
                    // FIN
                    _trace.ConnectionReadFin(ConnectionId);
                    break;
                }

                Input.Advance(bytesReceived);

                var flushTask = Input.FlushAsync();

                if (!flushTask.IsCompleted)
                {
                    _trace.ConnectionPause(ConnectionId);

                    await flushTask;

                    _trace.ConnectionResume(ConnectionId);
                }

                var result = flushTask.GetAwaiter().GetResult();
                if (result.IsCompleted)
                {
                    // Pipe consumer is shut down, so we stop writing
                    break;
                }
            }
            return awaitable.EventArgs.SocketError;
        }

        private async Task<Exception> DoSend()
        {
            Exception error = null;

            try
            {
                var socketError = await ProcessSends();

                if (socketError != SocketError.Success && socketError != SocketError.OperationAborted)
                    error = new SocketException((int)socketError);
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

        private async Task<SocketError> ProcessSends()
        {
            var awaitable = _sendAwaitable;
            while (true)
            {
                // Wait for data to write from the pipe producer
                var result = await Output.ReadAsync();
                var buffer = result.Buffer;

                if (result.IsCanceled)
                {
                    break;
                }

                var end = buffer.End;
                var isCompleted = result.IsCompleted;
                if (!buffer.IsEmpty)
                {
                    if (!await SendAsync(buffer))
                    {
                        break;
                    }
                }

                Output.AdvanceTo(end);

                if (isCompleted)
                {
                    break;
                }
            }
            return awaitable.EventArgs.SocketError;
        }

        private SocketAwaitable SendAsync(ReadOnlySequence<byte> buffers)
        {
            var awaitable = _sendAwaitable;
            if (buffers.IsSingleSegment)
            {
                return SendAsync(buffers.First);
            }

#if NETCOREAPP2_1
            if (!awaitable.EventArgs.MemoryBuffer.Equals(Memory<byte>.Empty))
#else
            if (awaitable.EventArgs.Buffer != null)
#endif
            {
                awaitable.EventArgs.SetBuffer(null, 0, 0);
            }

            awaitable.EventArgs.BufferList = GetBufferList(buffers);

            return _socket.SendAsync(awaitable);
        }

        private SocketAwaitable SendAsync(ReadOnlyMemory<byte> memory)
        {
            var awaitable = _sendAwaitable;
            // The BufferList getter is much less expensive then the setter.
            if (awaitable.EventArgs.BufferList != null)
            {
                awaitable.EventArgs.BufferList = null;
            }

#if NETCOREAPP2_1
            awaitable.EventArgs.SetBuffer(MemoryMarshal.AsMemory(memory));
#else
            var segment = memory.GetArray();

            awaitable.EventArgs.SetBuffer(segment.Array, segment.Offset, segment.Count);
#endif

            return _socket.SendAsync(awaitable);
        }

        private List<ArraySegment<byte>> _bufferList;

        private List<ArraySegment<byte>> GetBufferList(ReadOnlySequence<byte> buffer)
        {
            Debug.Assert(!buffer.IsEmpty);
            Debug.Assert(!buffer.IsSingleSegment);

            if (_bufferList == null)
            {
                _bufferList = new List<ArraySegment<byte>>();
            }
            else
            {
                // Buffers are pooled, so it's OK to root them until the next multi-buffer write.
                _bufferList.Clear();
            }

            foreach (var b in buffer)
            {
                _bufferList.Add(b.GetArray());
            }

            return _bufferList;
        }
    }
}