using Akka.Actor;
using Akka.Configuration;
using Neo.IO.Actors;
using Neo.Network.P2P.Payloads;

namespace Neo.Ledger
{
    public sealed partial class Blockchain
    {
        internal class BlockchainMailbox : PriorityMailbox
        {
            public BlockchainMailbox(Settings settings, Config config)
                : base(settings, config)
            {
            }

            protected internal override bool IsHighPriority(object message)
            {
                switch (message)
                {
                    case Header[] _:
                    case Block _:
                    case ConsensusPayload _:
                    case Terminated _:
                        return true;
                    default:
                        return false;
                }
            }
        }
    }
}
