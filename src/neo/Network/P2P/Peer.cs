using Akka.Actor;
using Microsoft.AspNetCore.Hosting;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.WebSockets;

namespace Neo.Network.P2P
{
    public abstract partial class Peer
    {
        public class Peers { public IEnumerable<IPEndPoint> EndPoints; }
        public class Connect { public IPEndPoint EndPoint; public bool IsTrusted = false; }
        private class Timer { }
        private class WsConnected { public WebSocket Socket; public IPEndPoint Remote; public IPEndPoint Local; }

        public const int DefaultMinDesiredConnections = 10;
        public const int DefaultMaxConnections = DefaultMinDesiredConnections * 4;

        private IActorRef tcp_listener;
        private IWebHost ws_host;
        private ICancelable timer;

        private static readonly HashSet<IPAddress> localAddresses = new HashSet<IPAddress>();
        private readonly Dictionary<IPAddress, int> ConnectedAddresses = new Dictionary<IPAddress, int>();
        /// <summary>
        /// A dictionary that stores the connected nodes.
        /// </summary>
        protected readonly ConcurrentDictionary<IActorRef, IPEndPoint> ConnectedPeers = new ConcurrentDictionary<IActorRef, IPEndPoint>();
        /// <summary>
        /// An ImmutableHashSet that stores the Peers received: 1) from other nodes or 2) from default file.
        /// If the number of desired connections is not enough, first try to connect with the peers from this set.
        /// </summary>
        protected ImmutableHashSet<IPEndPoint> UnconnectedPeers = ImmutableHashSet<IPEndPoint>.Empty;
        /// <summary>
        /// When a TCP connection request is sent to a peer, the peer will be added to the ImmutableHashSet.
        /// If a Tcp.Connected or a Tcp.CommandFailed (with TCP.Command of type Tcp.Connect) is received, the related peer will be removed.
        /// </summary>
        protected ImmutableHashSet<IPEndPoint> ConnectingPeers = ImmutableHashSet<IPEndPoint>.Empty;
        protected HashSet<IPAddress> TrustedIpAddresses { get; } = new HashSet<IPAddress>();

        public int ListenerTcpPort { get; private set; }
        public int ListenerWsPort { get; private set; }
        public int MaxConnectionsPerAddress { get; private set; } = 3;
        public int MinDesiredConnections { get; private set; } = DefaultMinDesiredConnections;
        public int MaxConnections { get; private set; } = DefaultMaxConnections;
        protected int UnconnectedMax { get; } = 1000;
        protected virtual int ConnectingMax
        {
            get
            {
                var allowedConnecting = MinDesiredConnections * 4;
                allowedConnecting = MaxConnections != -1 && allowedConnecting > MaxConnections
                    ? MaxConnections : allowedConnecting;
                return allowedConnecting - ConnectedPeers.Count;
            }
        }

        static Peer()
        {
            localAddresses.UnionWith(NetworkInterface.GetAllNetworkInterfaces().SelectMany(p => p.GetIPProperties().UnicastAddresses).Select(p => p.Address.Unmap()));
        }

        private static bool IsIntranetAddress(IPAddress address)
        {
            byte[] data = address.MapToIPv4().GetAddressBytes();
            uint value = BinaryPrimitives.ReadUInt32BigEndian(data);
            return (value & 0xff000000) == 0x0a000000 || (value & 0xff000000) == 0x7f000000 || (value & 0xfff00000) == 0xac100000 || (value & 0xffff0000) == 0xc0a80000 || (value & 0xffff0000) == 0xa9fe0000;
        }


        protected abstract Props ProtocolProps(object connection, IPEndPoint remote, IPEndPoint local);
    }
}
