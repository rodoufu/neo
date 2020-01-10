using Akka.Actor;
using Akka.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Neo.Consensus;

namespace Neo.Network.P2P
{
    public abstract partial class Peer
    {
        public abstract class PeerActor : UntypedActor
        {
            private Peer peer;
            private static readonly IActorRef tcp_manager = Context.System.Tcp();
            protected ActorSelection Connections => Context.ActorSelection("connection_*");

            public PeerActor(Peer peer)
            {
                this.peer = peer;
            }

            protected override void OnReceive(object message)
            {
                switch (message)
                {
                    case ChannelsConfig config:
                        OnStart(config);
                        break;
                    case ConsensusService.Timer _:
                        OnTimer();
                        break;
                    case Peer.Peers peers:
                        AddPeers(peers.EndPoints);
                        break;
                    case Peer.Connect connect:
                        ConnectToPeer(connect.EndPoint, connect.IsTrusted);
                        break;
                    case WsConnected ws:
                        OnWsConnected(ws.Socket, ws.Remote, ws.Local);
                        break;
                    case Tcp.Connected connected:
                        OnTcpConnected(((IPEndPoint) connected.RemoteAddress).Unmap(),
                            ((IPEndPoint) connected.LocalAddress).Unmap());
                        break;
                    case Tcp.Bound _:
                        peer.tcp_listener = Sender;
                        break;
                    case Tcp.CommandFailed commandFailed:
                        OnTcpCommandFailed(commandFailed.Cmd);
                        break;
                    case Terminated terminated:
                        OnTerminated(terminated.ActorRef);
                        break;
                }
            }

            private void OnStart(ChannelsConfig config)
            {
                peer.ListenerTcpPort = config.Tcp?.Port ?? 0;
                peer.ListenerWsPort = config.WebSocket?.Port ?? 0;

                peer.MinDesiredConnections = config.MinDesiredConnections;
                peer.MaxConnections = config.MaxConnections;
                peer.MaxConnectionsPerAddress = config.MaxConnectionsPerAddress;

                // schedule time to trigger `OnTimer` event every TimerMillisecondsInterval ms
                peer.timer = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(0, 5000, Context.Self, new Timer(),
                    ActorRefs.NoSender);
                if ((peer.ListenerTcpPort > 0 || peer.ListenerWsPort > 0)
                    && localAddresses.All(p => !p.IsIPv4MappedToIPv6 || IsIntranetAddress(p))
                    && UPnP.Discover())
                {
                    try
                    {
                        localAddresses.Add(UPnP.GetExternalIP());

                        if (peer.ListenerTcpPort > 0) UPnP.ForwardPort(peer.ListenerTcpPort, ProtocolType.Tcp, "NEO Tcp");
                        if (peer.ListenerWsPort > 0) UPnP.ForwardPort(peer.ListenerWsPort, ProtocolType.Tcp, "NEO WebSocket");
                    }
                    catch
                    {
                    }
                }

                if (peer.ListenerTcpPort > 0)
                {
                    tcp_manager.Tell(new Tcp.Bind(Self, config.Tcp, options: new[] {new Inet.SO.ReuseAddress(true)}));
                }

                if (peer.ListenerWsPort > 0)
                {
                    var host = "*";

                    if (!config.WebSocket.Address.GetAddressBytes().SequenceEqual(IPAddress.Any.GetAddressBytes()))
                    {
                        // Is not for all interfaces
                        host = config.WebSocket.Address.ToString();
                    }

                    peer.ws_host = new WebHostBuilder().UseKestrel().UseUrls($"http://{host}:{peer.ListenerWsPort}")
                        .Configure(app => app.UseWebSockets().Run(ProcessWebSocketAsync)).Build();
                    peer.ws_host.Start();
                }
            }

            private void OnTimer()
            {
                // Check if the number of desired connections is already enough
                if (peer.ConnectedPeers.Count >= peer.MinDesiredConnections) return;

                // If there aren't available UnconnectedPeers, it triggers an abstract implementation of NeedMorePeers
                if (peer.UnconnectedPeers.Count == 0)
                    NeedMorePeers(peer.MinDesiredConnections - peer.ConnectedPeers.Count);
                IPEndPoint[] endpoints = peer.UnconnectedPeers.Take(peer.MinDesiredConnections - peer.ConnectedPeers.Count).ToArray();
                ImmutableInterlocked.Update(ref peer.UnconnectedPeers, p => p.Except(endpoints));
                foreach (IPEndPoint endpoint in endpoints)
                {
                    ConnectToPeer(endpoint);
                }
            }

            /// <summary>
            /// Tries to add a set of peers to the immutable ImmutableHashSet of UnconnectedPeers.
            /// </summary>
            /// <param name="peers">Peers that the method will try to add (union) to (with) UnconnectedPeers.</param>
            protected void AddPeers(IEnumerable<IPEndPoint> peers)
            {
                if (peer.UnconnectedPeers.Count < peer.UnconnectedMax)
                {
                    // Do not select peers to be added that are already on the ConnectedPeers
                    // If the address is the same, the ListenerTcpPort should be different
                    peers = peers.Where(p =>
                        (p.Port != peer.ListenerTcpPort || !localAddresses.Contains(p.Address)) &&
                        !peer.ConnectedPeers.Values.Contains(p));
                    ImmutableInterlocked.Update(ref peer.UnconnectedPeers, p => p.Union(peers));
                }
            }

            protected void ConnectToPeer(IPEndPoint endPoint, bool isTrusted = false)
            {
                endPoint = endPoint.Unmap();
                // If the address is the same, the ListenerTcpPort should be different, otherwise, return
                if (endPoint.Port == peer.ListenerTcpPort && localAddresses.Contains(endPoint.Address)) return;

                if (isTrusted) peer.TrustedIpAddresses.Add(endPoint.Address);
                // If connections with the peer greater than or equal to MaxConnectionsPerAddress, return.
                if (peer.ConnectedAddresses.TryGetValue(endPoint.Address, out int count) &&
                    count >= peer.MaxConnectionsPerAddress)
                    return;
                if (peer.ConnectedPeers.Values.Contains(endPoint)) return;
                ImmutableInterlocked.Update(ref peer.ConnectingPeers, p =>
                {
                    if ((p.Count >= peer.ConnectingMax && !isTrusted) || p.Contains(endPoint)) return p;
                    tcp_manager.Tell(new Tcp.Connect(endPoint));
                    return p.Add(endPoint);
                });
            }

            private void OnWsConnected(WebSocket ws, IPEndPoint remote, IPEndPoint local)
            {
                peer.ConnectedAddresses.TryGetValue(remote.Address, out int count);
                if (count >= peer.MaxConnectionsPerAddress)
                {
                    ws.Abort();
                }
                else
                {
                    peer.ConnectedAddresses[remote.Address] = count + 1;
                    Context.ActorOf(ProtocolProps(ws, remote, local), $"connection_{Guid.NewGuid()}");
                }
            }

            /// <summary>
            /// Will be triggered when a Tcp.Connected message is received.
            /// If the conditions are met, the remote endpoint will be added to ConnectedPeers.
            /// Increase the connection number with the remote endpoint by one.
            /// </summary>
            /// <param name="remote">The remote endpoint of TCP connection.</param>
            /// <param name="local">The local endpoint of TCP connection.</param>
            private void OnTcpConnected(IPEndPoint remote, IPEndPoint local)
            {
                ImmutableInterlocked.Update(ref peer.ConnectingPeers, p => p.Remove(remote));
                if (peer.MaxConnections != -1 && peer.ConnectedPeers.Count >= peer.MaxConnections &&
                    !peer.TrustedIpAddresses.Contains(remote.Address))
                {
                    Sender.Tell(Tcp.Abort.Instance);
                    return;
                }

                peer.ConnectedAddresses.TryGetValue(remote.Address, out int count);
                if (count >= peer.MaxConnectionsPerAddress)
                {
                    Sender.Tell(Tcp.Abort.Instance);
                }
                else
                {
                    peer.ConnectedAddresses[remote.Address] = count + 1;
                    IActorRef connection = Context.ActorOf(ProtocolProps(Sender, remote, local),
                        $"connection_{Guid.NewGuid()}");
                    Context.Watch(connection);
                    Sender.Tell(new Tcp.Register(connection));
                    peer.ConnectedPeers.TryAdd(connection, remote);
                }
            }

            /// <summary>
            /// Will be triggered when a Tcp.CommandFailed message is received.
            /// If it's a Tcp.Connect command, remove the related endpoint from ConnectingPeers.
            /// </summary>
            /// <param name="cmd">Tcp.Command message/event.</param>
            private void OnTcpCommandFailed(Tcp.Command cmd)
            {
                switch (cmd)
                {
                    case Tcp.Connect connect:
                        ImmutableInterlocked.Update(ref peer.ConnectingPeers,
                            p => p.Remove(((IPEndPoint) connect.RemoteAddress).Unmap()));
                        break;
                }
            }

            private void OnTerminated(IActorRef actorRef)
            {
                if (peer.ConnectedPeers.TryRemove(actorRef, out IPEndPoint endPoint))
                {
                    peer.ConnectedAddresses.TryGetValue(endPoint.Address, out int count);
                    if (count > 0) count--;
                    if (count == 0)
                        peer.ConnectedAddresses.Remove(endPoint.Address);
                    else
                        peer.ConnectedAddresses[endPoint.Address] = count;
                }
            }

            protected override void PostStop()
            {
                peer.timer.CancelIfNotNull();
                peer.ws_host?.Dispose();
                peer.tcp_listener?.Tell(Tcp.Unbind.Instance);
                base.PostStop();
            }

            private async Task ProcessWebSocketAsync(HttpContext context)
            {
                if (!context.WebSockets.IsWebSocketRequest) return;
                WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();
                Self.Tell(new WsConnected
                {
                    Socket = ws,
                    Remote = new IPEndPoint(context.Connection.RemoteIpAddress, context.Connection.RemotePort),
                    Local = new IPEndPoint(context.Connection.LocalIpAddress, context.Connection.LocalPort)
                });
            }

            protected abstract Props ProtocolProps(object connection, IPEndPoint remote, IPEndPoint local);

            /// <summary>
            /// Abstract method for asking for more peers. Currently triggered when UnconnectedPeers is empty.
            /// </summary>
            /// <param name="count">Number of peers that are being requested.</param>
            protected abstract void NeedMorePeers(int count);
        }
    }
}
