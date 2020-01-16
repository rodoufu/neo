using Akka.Actor;
using Neo.Ledger;
using System.Net;

namespace Neo.Network.P2P
{
    public partial class LocalNode
    {
        public class LocalNodeActor : PeerActor
        {
            private readonly NeoContainer neoContainer;
            private readonly LocalNode localNode;

            public LocalNodeActor(NeoContainer neoContainer, LocalNode localNode) : base(localNode)
            {
                this.neoContainer = neoContainer;
                this.localNode = localNode;
            }

            protected override void OnReceive(object message)
            {
                base.OnReceive(message);
                switch (message)
                {
                    case Message msg:
                        localNode.BroadcastMessage(msg);
                        break;
                    case LocalNode.Relay relay:
                        localNode.OnRelay(relay.Inventory);
                        break;
                    case LocalNode.RelayDirectly relay:
                        localNode.OnRelayDirectly(relay.Inventory);
                        break;
                    case LocalNode.SendDirectly send:
                        localNode.OnSendDirectly(send.Inventory);
                        break;
                    case RelayResultReason _:
                        break;
                }
            }

            protected override Props ProtocolProps(object connection, IPEndPoint remote, IPEndPoint local) =>
                neoContainer.ResolveRemoteNodeProps(connection, remote, local);


            protected override void NeedMorePeers(int count)
            {
                localNode.NeedMorePeers(count, this);
            }
        }
    }
}
