# RawCommunication prototype

The overall goal is to implement a fairly generic networking library, and then use it to build a peer-to-peer chat sample. This idea was initially discussed on [aspnet/SignalR#1453](https://github.com/aspnet/SignalR/issues/1453).

This prototype is meant to assist in the current refactoring of the [Protocols](https://github.com/aspnet/KestrelHttpServer/tree/1f8591184e63467dc56b7252d3a340be8fee42b2/src/Protocols.Abstractions)/[Sockets](https://github.com/aspnet/SignalR/tree/b3a33efeaea9d99a0541be9b98104a2d72f87579/src/Microsoft.AspNetCore.Sockets.Abstractions) layer of [SignalR](https://github.com/aspnet/SignalR/tree/b3a33efeaea9d99a0541be9b98104a2d72f87579) and [Kestrel](https://github.com/aspnet/KestrelHttpServer/tree/1f8591184e63467dc56b7252d3a340be8fee42b2).

However, to keep it simply, it only relies on the .NET framework (and the Logging library).

**Getting Started**

1. Compile.
2. Run many instances of the `PeerChat` app. It uses UDP broadcasting to enable the instances to find each others. So, it should work when running on different computers. If you run many instances on the same computer, only the first instance will be able to find the next instances. All these next instances will only connect to the first instance (which is fine if you only have two instances).
3. Type a message and press Enter to send it to the other peers.
4. Type Enter without writing anything to gracefully stop the instance.

**Architecture**

`RawCommunication.Net` is a simple [Sockets](https://docs.microsoft.com/en-us/dotnet/api/system.net.sockets.socket)-based library with the following classes:
- `SocketAwaitable`: Similar to the [Kestrel's version](https://github.com/aspnet/KestrelHttpServer/blob/1f8591184e63467dc56b7252d3a340be8fee42b2/src/Kestrel.Transport.Sockets/Internal/SocketAwaitable.cs), however, it returns a bool for success instead of throwing. And there are extensions method to make calling it simpler. This makes the high-performance [SocketAsyncEventArgs](https://docs.microsoft.com/en-us/dotnet/api/system.net.sockets.socketasynceventargs) fairly elegant to use.
- `Broadcast` and `Transport`: Based on [Kestrel's SocketTransport](https://github.com/aspnet/KestrelHttpServer/blob/1f8591184e63467dc56b7252d3a340be8fee42b2/src/Kestrel.Transport.Sockets/SocketTransport.cs). `Broadcast` sends and receives UDP broadcast messages. `Transport` accepts incoming connections and initiates outgoing connections.
- `Connection`: Based on [Kestrel's SocketConnection](https://github.com/aspnet/KestrelHttpServer/blob/1f8591184e63467dc56b7252d3a340be8fee42b2/src/Kestrel.Transport.Sockets/Internal/SocketConnection.cs). Sends and receives bytes through its open socket.

`RawCommunication.PeerChat.App` is a console application that implements a simple peer-to-peer chat.

**Limitations**

This library doesn't depend on the new memory/pipelines libraries, it instead simulates how they work.
So, to keep it simple, it relies on delegates to wire up the classes. And the `Chat` class has an awkward implementation for its `ReadAsync()`.

I also don't like that it `throw new SocketException()` when a connection/listener is closed. Ideally, it should just exit the loop. But that requires some refactoring or duplication of code for exception handling.

And `Broadcast.OnBroadcastReceived()` should not be `async void`.
