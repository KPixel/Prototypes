using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RawCommunication.Net;

namespace RawCommunication.PeerChat
{
    internal static class Program
    {
        private static async Task Main()
        {
            var trace = new ConsoleTrace();
            var chat = new Chat(trace);
            await chat.BindAsync();

            string message;
            while ((message = Console.ReadLine())?.Length > 0)
                chat.SendMessageToAll(message);

            await chat.UnbindAsync();
            await chat.StopAsync();
        }
    }



    public class ConsoleTrace : ITrace
    {
        private static void Log(string message)
        {
            var log = $"{DateTime.Now.ToLongTimeString()} {message}";
            Debug.WriteLine(log);
            Console.WriteLine(log);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            Log($"[{logLevel}] {formatter?.Invoke(state, exception)}");
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            throw new NotImplementedException();
        }

        public void AddressInUse()
        {
            Log("AddressInUse: Another instance is already running on this computer so we cannot listen for incoming peers.");
        }

        public void Bind(IPEndPoint endPoint)
        {
            Log($"Bind {endPoint}");
        }

        public void ConnectionStarting(string connectionId)
        {
            Log($"ConnectionStarting {connectionId}");
        }

        public void ConnectionStopped(string connectionId, Exception receiveError, Exception sendError)
        {
            Log($"ConnectionStopped {connectionId} {receiveError} {sendError}");
        }

        public void ConnectionReceivedMessage(string connectionId, string message)
        {
            Log($"{connectionId} says: {message}");
        }

        public void ConnectionReceiveFromFin(string connectionId)
        {
            Log($"ConnectionReceiveFromFin {connectionId}");
        }

        public void ConnectionReadFin(string connectionId)
        {
            Log($"ConnectionReadFin {connectionId}");
        }

        public void ConnectionWriteFin(string connectionId)
        {
            Log($"ConnectionWriteFin {connectionId}");
        }

        public void ConnectionError(string connectionId, Exception ex)
        {
            Log($"ConnectionError {connectionId}: {ex}");
        }

        public void ConnectionReset(string connectionId)
        {
            Log($"ConnectionReset {connectionId}");
        }
    }
}