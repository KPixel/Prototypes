using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RawPipelines.Net;
using AddressInUseException = System.InvalidOperationException;

namespace RawPipelines.PeerChat
{
    public class Chat
    {
        private readonly ITrace _trace;
        private readonly Guid _id;
        private readonly List<Connection> _connections;
        private readonly Transport _transport;
        private readonly Broadcast _broadcast;

        public Chat(ITrace trace)
        {
            Debug.Assert(trace != null);

            _trace = trace;
            _id = Guid.NewGuid();
            _connections = new List<Connection>();

            _transport = new Transport(new IPEndPoint(IPAddress.Any, 0), _trace);
            _transport.ConnectionStarting += (s, e) => OnConnectionStarting(e);
            _broadcast = new Broadcast(new IPEndPoint(IPAddress.Any, 59000), _trace);
            _broadcast.ReceivedFrom += (s, e) => OnBroadcastReceived(e.EndPoint, e.Buffer);
        }

        public async Task BindAsync()
        {
            await _transport.BindAsync();

            try
            {
                await _broadcast.BindAsync();
            }
            catch (AddressInUseException)
            {
                _trace.AddressInUse();
            }

            _trace.Bind(_transport.EndPoint);

            var socketError = await _broadcast.SendToAsync(new ArraySegment<byte>(ToBytes("Broadcast:" + _id + ":" + _transport.EndPoint.Port)));
            if (socketError != SocketError.Success)
                _trace.LogError($"broadcast.SendToAsync() failed: {socketError}");
        }

        public async Task UnbindAsync()
        {
            await _transport?.UnbindAsync();
            await _broadcast?.UnbindAsync();
        }

        public async Task StopAsync()
        {
            await _transport?.StopAsync();
            await _broadcast?.StopAsync();
        }

        private async void OnBroadcastReceived(EndPoint endPoint, ArraySegment<byte>  buffer)
        {
            var msgParts = Encoding.ASCII.GetString(buffer.Array, buffer.Offset, buffer.Count).Split(':');
            var remoteId = Guid.Parse(msgParts[1]);
            if (_id == remoteId) return;

            var remoteIP = ((IPEndPoint)endPoint).Address;
            var remotePort = int.Parse(msgParts[2]);
            var remoteEndpoint = new IPEndPoint(remoteIP, remotePort);
            var socketError = await _transport.ConnectAsync(remoteEndpoint);
            if (socketError != SocketError.Success)
                _trace.LogError($"transport.ConnectAsync() failed: {socketError}");
        }

        private void OnConnectionStopped(Connection connection)
        {
            _connections.Remove(connection);
            _trace.ConnectionStopped(connection.ConnectionId, connection.ReceiveError, connection.SendError);
        }

        private void OnConnectionStarting(Connection connection)
        {
            _connections.Add(connection);
            _trace.ConnectionStarting(connection.ConnectionId);

            connection.Transport.Input.OnWriterCompleted((c, s) => OnConnectionStopped(connection), null);

            _ = ReceiveFromPipeLoop(connection.Transport.Input, connection.ConnectionId);
        }

        public void SendMessageToAll(string message)
        {
            var data = ToBytes(message);
            foreach (var connection in _connections)
            {
                var pipeWriter = connection.Transport.Output;

                pipeWriter.Write(data);
                _ = pipeWriter.FlushAsync();
            }
        }

        private static byte[] ToBytes(string s)
        {
            return Encoding.UTF8.GetBytes(s);
        }

        private async Task ReceiveFromPipeLoop(PipeReader pipeReader, string connectionId)
        {
            try
            {
                while (true)
                {
                    var result = await pipeReader.ReadAsync();
                    var buffer = result.Buffer;

                    try
                    {
                        if (result.IsCanceled)
                        {
                            break;
                        }
                        if (!buffer.IsEmpty)
                        {
                            _trace.ConnectionReceivedMessage(connectionId, Encoding.UTF8.GetString(GetSpan(in buffer).ToArray()));
                        }
                        else if (result.IsCompleted)
                        {
                            break;
                        }
                    }
                    finally
                    {
                        pipeReader.AdvanceTo(buffer.End);
                    }
                }
            }
            finally
            {
                pipeReader.Complete();
            }
        }

        private static ReadOnlySpan<byte> GetSpan(in ReadOnlySequence<byte> lengthPrefixBuffer)
        {
            if (lengthPrefixBuffer.IsSingleSegment)
            {
                return lengthPrefixBuffer.First.Span;
            }
            return lengthPrefixBuffer.ToArray();
        }
    }
}