using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

namespace RawCommunication.Net
{
    public class SocketAwaitable : ICriticalNotifyCompletion, IDisposable
    {
        private static readonly Action CallbackCompleted = () => { };

        internal readonly SocketAsyncEventArgs EventArgs;
        private Action _callback;

        public SocketAwaitable GetAwaiter() => this;
        public bool IsCompleted => ReferenceEquals(_callback, CallbackCompleted);

        public SocketAwaitable()
        {
            EventArgs = new SocketAsyncEventArgs();
            EventArgs.Completed += (s, e) => Complete();
        }

        internal SocketAwaitable InvokeAsync(Func<SocketAsyncEventArgs, bool> func)
        {
            if (!func(EventArgs))
                Complete();
            return this;
        }

        private void Complete()
        {
            Interlocked.Exchange(ref _callback, CallbackCompleted)?.Invoke();
        }

        public void OnCompleted(Action continuation)
        {
            if (ReferenceEquals(_callback, CallbackCompleted)
                || ReferenceEquals(Interlocked.CompareExchange(ref _callback, continuation, null), CallbackCompleted))
                continuation();
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            OnCompleted(continuation);
        }

        public bool GetResult()
        {
            Debug.Assert(_callback == CallbackCompleted);
            _callback = null;
            return EventArgs.SocketError == SocketError.Success;
        }

        public void Dispose()
        {
            EventArgs.Dispose();
        }
    }

    public static class SocketExtensions
    {
        public static SocketAwaitable AcceptAsync(this Socket socket, SocketAwaitable awaitable)
        {
            return awaitable.InvokeAsync(socket.AcceptAsync);
        }

        public static SocketAwaitable ConnectAsync(this Socket socket, SocketAwaitable awaitable)
        {
            return awaitable.InvokeAsync(socket.ConnectAsync);
        }

        public static SocketAwaitable ReceiveAsync(this Socket socket, SocketAwaitable awaitable)
        {
            return awaitable.InvokeAsync(socket.ReceiveAsync);
        }

        public static SocketAwaitable ReceiveFromAsync(this Socket socket, SocketAwaitable awaitable)
        {
            return awaitable.InvokeAsync(socket.ReceiveFromAsync);
        }

        public static SocketAwaitable SendAsync(this Socket socket, SocketAwaitable awaitable)
        {
            return awaitable.InvokeAsync(socket.SendAsync);
        }

        public static SocketAwaitable SendToAsync(this Socket socket, SocketAwaitable awaitable)
        {
            return awaitable.InvokeAsync(socket.SendToAsync);
        }
    }
}
