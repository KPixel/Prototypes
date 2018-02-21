using System;
using System.Net;
using Microsoft.Extensions.Logging;

namespace RawCommunication.Net
{
    public interface ITrace : ILogger
    {
        void AddressInUse();

        void Bind(IPEndPoint endPoint);

        void ConnectionStarting(string connectionId);

        void ConnectionStopped(string connectionId, Exception receiveError, Exception sendError);

        void ConnectionReceivedMessage(string connectionId, string message);

        void ConnectionReceiveFromFin(string connectionId);

        void ConnectionReadFin(string connectionId);

        void ConnectionWriteFin(string connectionId);

        void ConnectionError(string connectionId, Exception ex);

        void ConnectionReset(string connectionId);
    }
}
