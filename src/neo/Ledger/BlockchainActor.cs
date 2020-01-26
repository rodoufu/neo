using Akka.Actor;
using Neo.IO.Actors;
using Neo.IO.Caching;
using Neo.Network.P2P.Payloads;
using System.Linq;

namespace Neo.Ledger
{
    public sealed partial class Blockchain
    {
        public sealed class BlockchainActor : UntypedActor
        {
            private readonly Blockchain blockchain;

            public BlockchainActor(Blockchain blockchain)
            {
                this.blockchain = blockchain;
                lock (blockchain)
                {
                    blockchain.header_index.AddRange(blockchain.View.HeaderHashList.Find().OrderBy(p => (uint) p.Key)
                        .SelectMany(p => p.Value.Hashes));
                    blockchain.stored_header_count += (uint) blockchain.header_index.Count;
                    if (blockchain.stored_header_count == 0)
                    {
                        blockchain.header_index.AddRange(blockchain.View.Blocks.Find().OrderBy(p => p.Value.Index).Select(p => p.Key));
                    }
                    else
                    {
                        HashIndexState hashIndex = blockchain.View.HeaderHashIndex.Get();
                        if (hashIndex.Index >= blockchain.stored_header_count)
                        {
                            DataCache<UInt256, TrimmedBlock> cache = blockchain.View.Blocks;
                            for (UInt256 hash = hashIndex.Hash; hash != blockchain.header_index[(int) blockchain.stored_header_count - 1];)
                            {
                                blockchain.header_index.Insert((int) blockchain.stored_header_count, hash);
                                hash = cache[hash].PrevHash;
                            }
                        }
                    }

                    if (blockchain.header_index.Count == 0)
                    {
                        blockchain.Persist(GenesisBlock, Context);
                    }
                    else
                    {
                        blockchain.UpdateCurrentSnapshot();
                        blockchain.MemPool.LoadPolicy(blockchain.currentSnapshot);
                    }
                }
            }

            protected override void OnReceive(object message)
            {
                switch (message)
                {
                    case Import import:
                        blockchain.OnImport(import.Blocks, import.Verify, Sender, Context);
                        break;
                    case FillMemoryPool fill:
                        blockchain.OnFillMemoryPool(fill.Transactions, Sender);
                        break;
                    case Header[] headers:
                        blockchain.OnNewHeaders(headers, Sender);
                        break;
                    case Block block:
                        Sender.Tell(blockchain.OnNewBlock(block, Self, Context));
                        break;
                    case Transaction[] transactions:
                    {
                        // This message comes from a mempool's revalidation, already relayed
                        foreach (var tx in transactions) blockchain.OnNewTransaction(tx, false, Self, Sender);
                        break;
                    }

                    case Transaction transaction:
                        blockchain.OnNewTransaction(transaction, true, Self, Sender);
                        break;
                    case ParallelVerified parallelVerified:
                        blockchain.OnParallelVerified(parallelVerified, Sender);
                        break;
                    case ConsensusPayload payload:
                        Sender.Tell(blockchain.OnNewConsensus(payload));
                        break;
                    case Idle _:
                        if (blockchain.MemPool.ReVerifyTopUnverifiedTransactionsIfNeeded(MaxTxToReverifyPerIdle,
                            blockchain.currentSnapshot))
                            Self.Tell(Idle.Instance, ActorRefs.NoSender);
                        break;
                }
            }

            protected override void PostStop()
            {
                base.PostStop();
                blockchain.currentSnapshot?.Dispose();
            }
        }
    }
}