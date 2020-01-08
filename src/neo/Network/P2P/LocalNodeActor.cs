using Akka.Actor;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Akka.DI.Core;
using Neo.Consensus;
using System.Threading.Tasks;

namespace Neo.Network.P2P
{
    public partial class LocalNode
    {
        public class LocalNodeActor : PeerActor
        {
            private const int MaxCountFromSeedList = 5;

            private readonly LocalNode localNode;
            private readonly IActorRef blockchainActorRef;
            private readonly IActorRef consensusServiceActor;

            public LocalNodeActor(/*NeoContainer neoContainer, LocalNode localNode*/) : base(null/*localNode*/)
            {
//                this.blockchainActorRef = neoContainer.BlockchainActor;
//                this.consensusServiceActor = neoContainer.ConsensusServiceActor;
//                this.localNode = localNode;
            }

            /// <summary>
            /// Override of abstract class that is triggered when <see cref="Peer.UnconnectedPeers"/> is empty.
            /// Performs a BroadcastMessage with the command `MessageCommand.GetAddr`, which, eventually, tells all known connections.
            /// If there are no connected peers it will try with the default, respecting MaxCountFromSeedList limit.
            /// </summary>
            /// <param name="count">The count of peers required</param>
            protected override void NeedMorePeers(int count)
            {
                count = Math.Max(count, MaxCountFromSeedList);
                if (localNode.ConnectedPeers.Count > 0)
                {
                    BroadcastMessage(MessageCommand.GetAddr);
                }
                else
                {
                    // Will call AddPeers with default SeedList set cached on <see cref="ProtocolSettings"/>.
                    // It will try to add those, sequentially, to the list of currently unconnected ones.

                    Random rand = new Random();
                    AddPeers(localNode.SeedList.Where(u => u != null).OrderBy(p => rand.Next()).Take(count));
                }
            }

            protected override void OnReceive(object message)
            {
                base.OnReceive(message);
                switch (message)
                {
                    case Message msg:
                        BroadcastMessage(msg);
                        break;
                    case LocalNode.Relay relay:
                        OnRelay(relay.Inventory);
                        break;
                    case LocalNode.RelayDirectly relay:
                        OnRelayDirectly(relay.Inventory);
                        break;
                    case LocalNode.SendDirectly send:
                        OnSendDirectly(send.Inventory);
                        break;
                    case RelayResultReason _:
                        break;
                }
            }

            /// <summary>
            /// Packs a MessageCommand to a full Message with an optional ISerializable payload.
            /// Forwards it to <see cref="BroadcastMessage(Message message)"/>.
            /// </summary>
            /// <param name="command">The message command to be packed.</param>
            /// <param name="payload">Optional payload to be Serialized along the message.</param>
            private void BroadcastMessage(MessageCommand command, ISerializable payload = null)
            {
                BroadcastMessage(Message.Create(command, payload));
            }

            /// <summary>
            /// Broadcast a message to all connected nodes, namely <see cref="Connections"/>.
            /// </summary>
            /// <param name="message">The message to be broadcasted.</param>
            private void BroadcastMessage(Message message) => localNode.SendToRemoteNodes(message);

            /// <summary>
            /// For Transaction type of IInventory, it will tell Transaction to the actor of Consensus.
            /// Otherwise, tell the inventory to the actor of Blockchain.
            /// There are, currently, three implementations of IInventory: TX, Block and ConsensusPayload.
            /// </summary>
            /// <param name="inventory">The inventory to be relayed.</param>
            private void OnRelay(IInventory inventory)
            {
                if (inventory is Transaction transaction)
                    consensusServiceActor?.Tell(transaction);
                blockchainActorRef.Tell(inventory);
            }

            private void OnRelayDirectly(IInventory inventory)
            {
                var message = new RemoteNode.Relay {Inventory = inventory};
                // When relaying a block, if the block's index is greater than 'LastBlockIndex' of the RemoteNode, relay the block;
                // otherwise, don't relay.
                if (inventory is Block block)
                {
                    foreach (KeyValuePair<IActorRef, RemoteNode> kvp in localNode.RemoteNodes)
                    {
                        if (block.Index > kvp.Value.LastBlockIndex)
                            kvp.Key.Tell(message);
                    }
                }
                else
                    localNode.SendToRemoteNodes(message);
            }

            private void OnSendDirectly(IInventory inventory) => localNode.SendToRemoteNodes(inventory);

            protected override Props ProtocolProps(object connection, IPEndPoint remote, IPEndPoint local) =>
                localNode.neoContainer.ResolveNodeProps(connection, remote, local);
        }
    }
}
