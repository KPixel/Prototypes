using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using RawCommunication.Net;
using AddressInUseException = System.InvalidOperationException;

namespace RawCommunication.PeerChat
{
    public class Chat
    {
        private readonly ITrace _trace;
        private readonly Guid _id;
        private readonly Dictionary<Connection, ConcurrentQueue<ArraySegment<byte>>> _connections;
        private readonly Transport _transport;
        private readonly Broadcast _broadcast;

        public Chat(ITrace trace)
        {
            Debug.Assert(trace != null);

            _trace = trace;
            _id = Guid.NewGuid();
            _connections = new Dictionary<Connection, ConcurrentQueue<ArraySegment<byte>>>();

            _transport = new Transport(new IPEndPoint(IPAddress.Any, 0), _trace);
            _transport.ConnectionStarting = OnConnectionStarting;
            _broadcast = new Broadcast(new IPEndPoint(IPAddress.Any, 59000), _trace);
            _broadcast.ReceivedFrom = OnBroadcastReceived;
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

            await _broadcast.SendToAsync(ToBuffer("Broadcast:" + _id + ":" + _transport.EndPoint.Port));
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
            await _transport.ConnectAsync(remoteEndpoint);
        }

        private void OnConnectionStopped(Connection connection, Exception receiveError, Exception sendError)
        {
            _connections.Remove(connection);
            _trace.ConnectionStopped(connection.ConnectionId, receiveError, sendError);
        }

        private void OnConnectionStarting(Connection connection)
        {
            var queue = new ConcurrentQueue<ArraySegment<byte>>();
            _connections.Add(connection, queue);
            _trace.ConnectionStarting(connection.ConnectionId);

            connection.Completed = (receiveError, sendError) => OnConnectionStopped(connection, receiveError, sendError);
            connection.Received = buffer => _trace.ConnectionReceivedMessage(connection.ConnectionId, Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count));
            connection.ReadAsync = () => ReadAsync(connection, queue);
        }

        private static async Task<ArraySegment<byte>> ReadAsync(Connection connection, ConcurrentQueue<ArraySegment<byte>> queue)
        {
            while (connection.IsConnected)
            {
                if (queue.TryDequeue(out var buffer))
                    return buffer;
                await Task.Delay(1);
            }
#if NETCOREAPP2_0
            return ArraySegment<byte>.Empty;
#else
            return new ArraySegment<byte>();
#endif
        }

        public void SendMessageToAll(string message)
        {
            var buffer = ToBuffer(message);
            foreach (var connection in _connections)
                connection.Value.Enqueue(buffer);
        }

        private static ArraySegment<byte> ToBuffer(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            return new ArraySegment<byte>(bytes);
        }
    }
}