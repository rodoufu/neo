using Akka.Actor;
using Neo.IO.Actors;
using Neo.IO.Caching;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.VM;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
                        blockchain.header_index.AddRange(blockchain.View.Blocks.Find().OrderBy(p => p.Value.Index)
                            .Select(p => p.Key));
                    }
                    else
                    {
                        HashIndexState hashIndex = blockchain.View.HeaderHashIndex.Get();
                        if (hashIndex.Index >= blockchain.stored_header_count)
                        {
                            DataCache<UInt256, TrimmedBlock> cache = blockchain.View.Blocks;
                            for (UInt256 hash = hashIndex.Hash;
                                hash != blockchain.header_index[(int) blockchain.stored_header_count - 1];)
                            {
                                blockchain.header_index.Insert((int) blockchain.stored_header_count, hash);
                                hash = cache[hash].PrevHash;
                            }
                        }
                    }

                    if (blockchain.header_index.Count == 0)
                    {
                        Persist(GenesisBlock);
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
                        OnImport(import.Blocks);
                        break;
                    case FillMemoryPool fill:
                        OnFillMemoryPool(fill.Transactions);
                        break;
                    case Header[] headers:
                        OnNewHeaders(headers);
                        break;
                    case Block block:
                        Sender.Tell(OnNewBlock(block));
                        break;
                    case Transaction[] transactions:
                    {
                        // This message comes from a mempool's revalidation, already relayed
                        foreach (var tx in transactions) OnNewTransaction(tx, false);
                        break;
                    }

                    case Transaction transaction:
                        OnNewTransaction(transaction, true);
                        break;
                    case ParallelVerified parallelVerified:
                        OnParallelVerified(parallelVerified);
                        break;
                    case ConsensusPayload payload:
                        Sender.Tell(OnNewConsensus(payload));
                        break;
                    case Idle _:
                        if (blockchain.MemPool.ReVerifyTopUnverifiedTransactionsIfNeeded(MaxTxToReverifyPerIdle, blockchain.currentSnapshot))
                            Self.Tell(Idle.Instance, ActorRefs.NoSender);
                        break;
                }
            }

            protected override void PostStop()
            {
                base.PostStop();
                blockchain.currentSnapshot?.Dispose();
            }

            private void OnImport(IEnumerable<Block> blocks)
            {
                foreach (Block block in blocks)
                {
                    if (block.Index <= blockchain.Height) continue;
                    if (block.Index != blockchain.Height + 1)
                        throw new InvalidOperationException();
                    Persist(block);
                    blockchain.SaveHeaderHashList();
                }

                Sender.Tell(new ImportCompleted());
            }

            private void Persist(Block block)
            {
                using (SnapshotView snapshot = blockchain.GetSnapshot())
                {
                    List<ApplicationExecuted> all_application_executed = new List<ApplicationExecuted>();
                    snapshot.PersistingBlock = block;
                    if (block.Index > 0)
                    {
                        using (ApplicationEngine engine =
                            new ApplicationEngine(TriggerType.System, null, snapshot, 0, true))
                        {
                            engine.LoadScript(onPersistNativeContractScript);
                            if (engine.Execute() != VMState.HALT) throw new InvalidOperationException();
                            ApplicationExecuted application_executed = new ApplicationExecuted(engine);
                            Context.System.EventStream.Publish(application_executed);
                            all_application_executed.Add(application_executed);
                        }
                    }

                    snapshot.Blocks.Add(block.Hash, block.Trim());
                    foreach (Transaction tx in block.Transactions)
                    {
                        var state = new TransactionState
                        {
                            BlockIndex = block.Index,
                            Transaction = tx
                        };

                        snapshot.Transactions.Add(tx.Hash, state);

                        using (ApplicationEngine engine = new ApplicationEngine(TriggerType.Application, tx,
                            snapshot.Clone(), tx.SystemFee))
                        {
                            engine.LoadScript(tx.Script);
                            state.VMState = engine.Execute();
                            if (state.VMState == VMState.HALT)
                            {
                                engine.Snapshot.Commit();
                            }

                            ApplicationExecuted application_executed = new ApplicationExecuted(engine);
                            Context.System.EventStream.Publish(application_executed);
                            all_application_executed.Add(application_executed);
                        }
                    }

                    snapshot.BlockHashIndex.GetAndChange().Set(block);
                    if (block.Index == blockchain.header_index.Count)
                    {
                        blockchain.header_index.Add(block.Hash);
                        snapshot.HeaderHashIndex.GetAndChange().Set(block);
                    }

                    foreach (IPersistencePlugin plugin in Plugin.PersistencePlugins)
                        plugin.OnPersist(snapshot, all_application_executed);
                    snapshot.Commit();
                    List<Exception> commitExceptions = null;
                    foreach (IPersistencePlugin plugin in Plugin.PersistencePlugins)
                    {
                        try
                        {
                            plugin.OnCommit(snapshot);
                        }
                        catch (Exception ex)
                        {
                            if (plugin.ShouldThrowExceptionFromCommit(ex))
                            {
                                if (commitExceptions == null)
                                    commitExceptions = new List<Exception>();

                                commitExceptions.Add(ex);
                            }
                        }
                    }

                    if (commitExceptions != null) throw new AggregateException(commitExceptions);
                }

                blockchain.UpdateCurrentSnapshot();
                OnPersistCompleted(block);
            }

            private void OnPersistCompleted(Block block)
            {
                blockchain.block_cache.Remove(block.Hash);
                blockchain.MemPool.UpdatePoolForBlockPersisted(block, blockchain.currentSnapshot);
                Context.System.EventStream.Publish(new PersistCompleted {Block = block});
            }

            private void OnFillMemoryPool(IEnumerable<Transaction> transactions)
            {
                // Invalidate all the transactions in the memory pool, to avoid any failures when adding new transactions.
                blockchain.MemPool.InvalidateAllTransactions();

                // Add the transactions to the memory pool
                foreach (var tx in transactions)
                {
                    if (blockchain.View.ContainsTransaction(tx.Hash))
                        continue;
                    if (!NativeContract.Policy.CheckPolicy(tx, blockchain.currentSnapshot))
                        continue;
                    // First remove the tx if it is unverified in the pool.
                    blockchain.MemPool.TryRemoveUnVerified(tx.Hash, out _);
                    // Verify the the transaction
                    if (tx.Verify(blockchain.currentSnapshot, blockchain.MemPool.SendersFeeMonitor.GetSenderFee(tx.Sender)) !=
                        RelayResultReason.Succeed)
                        continue;
                    // Add to the memory pool
                    blockchain.MemPool.TryAdd(tx.Hash, tx);
                }
                // Transactions originally in the pool will automatically be reverified based on their priority.

                Sender.Tell(new FillCompleted());
            }

            private void OnNewHeaders(Header[] headers)
            {
                using (SnapshotView snapshot = blockchain.GetSnapshot())
                {
                    foreach (Header header in headers)
                    {
                        if (header.Index - 1 >= blockchain.header_index.Count) break;
                        if (header.Index < blockchain.header_index.Count) continue;
                        if (!header.Verify(snapshot)) break;
                        blockchain.header_index.Add(header.Hash);
                        snapshot.Blocks.Add(header.Hash, header.Trim());
                        snapshot.HeaderHashIndex.GetAndChange().Hash = header.Hash;
                        snapshot.HeaderHashIndex.GetAndChange().Index = header.Index;
                    }

                    blockchain.SaveHeaderHashList(snapshot);
                    snapshot.Commit();
                }

                blockchain.UpdateCurrentSnapshot();
                blockchain.taskManagerActor.Tell(new TaskManager.HeaderTaskCompleted(), Sender);
            }

            private RelayResultReason OnNewBlock(Block block)
            {
                if (block.Index <= blockchain.Height)
                    return RelayResultReason.AlreadyExists;
                if (blockchain.block_cache.ContainsKey(block.Hash))
                    return RelayResultReason.AlreadyExists;
                if (block.Index - 1 >= blockchain.header_index.Count)
                {
                    blockchain.AddUnverifiedBlockToCache(block);
                    return RelayResultReason.UnableToVerify;
                }

                if (block.Index == blockchain.header_index.Count)
                {
                    if (!block.Verify(blockchain.currentSnapshot))
                        return RelayResultReason.Invalid;
                }
                else
                {
                    if (!block.Hash.Equals(blockchain.header_index[(int) block.Index]))
                        return RelayResultReason.Invalid;
                }

                if (block.Index == blockchain.Height + 1)
                {
                    Block block_persist = block;
                    List<Block> blocksToPersistList = new List<Block>();
                    while (true)
                    {
                        blocksToPersistList.Add(block_persist);
                        if (block_persist.Index + 1 >= blockchain.header_index.Count) break;
                        UInt256 hash = blockchain.header_index[(int) block_persist.Index + 1];
                        if (!blockchain.block_cache.TryGetValue(hash, out block_persist)) break;
                    }

                    int blocksPersisted = 0;
                    foreach (Block blockToPersist in blocksToPersistList)
                    {
                        blockchain.block_cache_unverified.Remove(blockToPersist.Index);
                        Persist(blockToPersist);

                        // 15000 is the default among of seconds per block, while MilliSecondsPerBlock is the current
                        uint extraBlocks = (15000 - MillisecondsPerBlock) / 1000;

                        if (blocksPersisted++ < blocksToPersistList.Count - (2 + Math.Max(0, extraBlocks))) continue;
                        // Empirically calibrated for relaying the most recent 2 blocks persisted with 15s network
                        // Increase in the rate of 1 block per second in configurations with faster blocks

                        if (blockToPersist.Index + 100 >= blockchain.header_index.Count)
                            blockchain.localNodeActor.Tell(new LocalNode.RelayDirectly {Inventory = blockToPersist});
                    }

                    blockchain.SaveHeaderHashList();

                    if (blockchain.block_cache_unverified.TryGetValue(blockchain.Height + 1, out LinkedList<Block> unverifiedBlocks))
                    {
                        foreach (var unverifiedBlock in unverifiedBlocks)
                            Self.Tell(unverifiedBlock, ActorRefs.NoSender);
                        blockchain.block_cache_unverified.Remove(blockchain.Height + 1);
                    }
                }
                else
                {
                    blockchain.block_cache.Add(block.Hash, block);
                    if (block.Index + 100 >= blockchain.header_index.Count)
                        blockchain.localNodeActor.Tell(new LocalNode.RelayDirectly {Inventory = block});
                    if (block.Index == blockchain.header_index.Count)
                    {
                        blockchain.header_index.Add(block.Hash);
                        using (SnapshotView snapshot = blockchain.GetSnapshot())
                        {
                            snapshot.Blocks.Add(block.Hash, block.Header.Trim());
                            snapshot.HeaderHashIndex.GetAndChange().Set(block);
                            blockchain.SaveHeaderHashList(snapshot);
                            snapshot.Commit();
                        }

                        blockchain.UpdateCurrentSnapshot();
                    }
                }

                return RelayResultReason.Succeed;
            }

            private void OnNewTransaction(Transaction transaction, bool relay)
            {
                RelayResultReason reason;
                if (blockchain.ContainsTransaction(transaction.Hash))
                    reason = RelayResultReason.AlreadyExists;
                else if (!blockchain.MemPool.CanTransactionFitInPool(transaction))
                    reason = RelayResultReason.OutOfMemory;
                else
                    reason = transaction.VerifyForEachBlock(blockchain.currentSnapshot,
                        blockchain.MemPool.SendersFeeMonitor.GetSenderFee(transaction.Sender));
                if (reason == RelayResultReason.Succeed)
                {
                    Task.Run(() =>
                    {
                        return new ParallelVerified
                        {
                            Transaction = transaction,
                            ShouldRelay = relay,
                            VerifyResult = transaction.VerifyParallelParts(blockchain.currentSnapshot)
                        };
                    }).PipeTo(Self, Sender);
                }
                else
                {
                    Sender.Tell(reason);
                }
            }

            private RelayResultReason OnNewConsensus(ConsensusPayload payload)
            {
                if (!payload.Verify(blockchain.currentSnapshot)) return RelayResultReason.Invalid;
                blockchain.consensusServiceActor?.Tell(payload);
                blockchain.ConsensusRelayCache.Add(payload);
                blockchain.localNodeActor.Tell(new LocalNode.RelayDirectly {Inventory = payload});
                return RelayResultReason.Succeed;
            }

            private void OnParallelVerified(ParallelVerified parallelVerified)
            {
                RelayResultReason reason = parallelVerified.VerifyResult;
                if (reason == RelayResultReason.Succeed)
                {
                    if (blockchain.View.ContainsTransaction(parallelVerified.Transaction.Hash))
                        reason = RelayResultReason.AlreadyExists;
                    else if (!blockchain.MemPool.CanTransactionFitInPool(parallelVerified.Transaction))
                        reason = RelayResultReason.OutOfMemory;
                    else if (!blockchain.MemPool.TryAdd(parallelVerified.Transaction.Hash, parallelVerified.Transaction))
                        reason = RelayResultReason.OutOfMemory;
                    else if (parallelVerified.ShouldRelay)
                        blockchain.localNodeActor.Tell(new LocalNode.RelayDirectly {Inventory = parallelVerified.Transaction});
                }

                Sender.Tell(reason);
            }
        }
    }
}
