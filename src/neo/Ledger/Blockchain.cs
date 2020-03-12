using Akka.Actor;
using Neo.Cryptography.ECC;
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
using System.Threading;
using System.Threading.Tasks;

namespace Neo.Ledger
{
    public sealed partial class Blockchain
    {
        public partial class ApplicationExecuted { }
        public class PersistCompleted { public Block Block; }
        public class Import { public IEnumerable<Block> Blocks; public bool Verify = true; }
        public class ImportCompleted { }
        public class FillMemoryPool { public IEnumerable<Transaction> Transactions; }
        public class FillCompleted { }

        public static readonly uint MillisecondsPerBlock = ProtocolSettings.Default.MillisecondsPerBlock;
        public const uint DecrementInterval = 2000000;
        public const int MaxValidators = 1024;
        public static readonly uint[] GenerationAmount = { 6, 5, 4, 3, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
        public static readonly TimeSpan TimePerBlock = TimeSpan.FromMilliseconds(MillisecondsPerBlock);
        public static readonly ECPoint[] StandbyValidators = ProtocolSettings.Default.StandbyValidators.OfType<string>().Select(p => ECPoint.DecodePoint(p.HexToBytes(), ECCurve.Secp256r1)).ToArray();

        public static readonly Block GenesisBlock = new Block
        {
            PrevHash = UInt256.Zero,
            Timestamp = (new DateTime(2016, 7, 15, 15, 8, 21, DateTimeKind.Utc)).ToTimestampMS(),
            Index = 0,
            NextConsensus = GetConsensusAddress(StandbyValidators),
            Witness = new Witness
            {
                InvocationScript = Array.Empty<byte>(),
                VerificationScript = new[] { (byte)OpCode.PUSH1 }
            },
            ConsensusData = new ConsensusData
            {
                PrimaryIndex = 0,
                Nonce = 2083236893
            },
            Transactions = new[] { DeployNativeContracts() }
        };

        private readonly static Script onPersistNativeContractScript;
        private const int MaxTxToReverifyPerIdle = 10;
        private readonly List<UInt256> header_index = new List<UInt256>();
        private uint stored_header_count = 0;
        private readonly Dictionary<UInt256, Block> block_cache = new Dictionary<UInt256, Block>();
        private readonly Dictionary<uint, LinkedList<Block>> block_cache_unverified = new Dictionary<uint, LinkedList<Block>>();
        internal readonly RelayCache ConsensusRelayCache = new RelayCache(100);
        private SnapshotView currentSnapshot;

        public IStore Store { get; }
        public ReadOnlyView View { get; }
        public MemoryPool MemPool { get; }
        public uint Height => currentSnapshot.Height;
        public uint HeaderHeight => currentSnapshot.HeaderHeight;
        public UInt256 CurrentBlockHash => currentSnapshot.CurrentBlockHash;
        public UInt256 CurrentHeaderHash => currentSnapshot.CurrentHeaderHash;

        private readonly NeoContainer neoContainer;
        private IActorRef localNodeActor => neoContainer.LocalNodeActor;
        private IActorRef taskManagerActor => neoContainer.TaskManagerActor;
        private IActorRef consensusServiceActor => neoContainer.ConsensusServiceActor;

        static Blockchain()
        {
            GenesisBlock.RebuildMerkleRoot();

            NativeContract[] contracts = { NativeContract.GAS, NativeContract.NEO };
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                foreach (NativeContract contract in contracts)
                    sb.EmitAppCall(contract.Hash, "onPersist");

                onPersistNativeContractScript = sb.ToArray();
            }
        }

        public Blockchain(NeoContainer neoContainer, MemoryPool memoryPool, IStore store)
        {
            this.neoContainer = neoContainer;
            this.MemPool = memoryPool;
            this.Store = store;
            this.View = new ReadOnlyView(store);
        }

        public bool ContainsBlock(UInt256 hash)
        {
            if (block_cache.ContainsKey(hash)) return true;
            return View.ContainsBlock(hash);
        }

        public bool ContainsTransaction(UInt256 hash)
        {
            if (MemPool.ContainsKey(hash)) return true;
            return View.ContainsTransaction(hash);
        }

        private static Transaction DeployNativeContracts()
        {
            byte[] script;
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitSysCall(InteropService.Native.Deploy);
                script = sb.ToArray();
            }
            return new Transaction
            {
                Version = 0,
                Script = script,
                Sender = (new[] { (byte)OpCode.PUSH1 }).ToScriptHash(),
                SystemFee = 0,
                Attributes = new TransactionAttribute[0],
                Cosigners = new Cosigner[0],
                Witnesses = new[]
                {
                    new Witness
                    {
                        InvocationScript = Array.Empty<byte>(),
                        VerificationScript = new[] { (byte)OpCode.PUSH1 }
                    }
                }
            };
        }

        public Block GetBlock(uint index)
        {
            if (index == 0) return GenesisBlock;
            UInt256 hash = GetBlockHash(index);
            if (hash == null) return null;
            return GetBlock(hash);
        }

        public Block GetBlock(UInt256 hash)
        {
            if (block_cache.TryGetValue(hash, out Block block))
                return block;
            return View.GetBlock(hash);
        }

        public UInt256 GetBlockHash(uint index)
        {
            if (header_index.Count <= index) return null;
            return header_index[(int)index];
        }

        public static UInt160 GetConsensusAddress(ECPoint[] validators)
        {
            return Contract.CreateMultiSigRedeemScript(validators.Length - (validators.Length - 1) / 3, validators).ToScriptHash();
        }

        public Header GetHeader(uint index)
        {
            if (index == 0) return GenesisBlock.Header;
            UInt256 hash = GetBlockHash(index);
            if (hash == null) return null;
            return GetHeader(hash);
        }

        public Header GetHeader(UInt256 hash)
        {
            if (block_cache.TryGetValue(hash, out Block block))
                return block.Header;
            return View.GetHeader(hash);
        }

        public UInt256 GetNextBlockHash(UInt256 hash)
        {
            Header header = GetHeader(hash);
            if (header == null) return null;
            return GetBlockHash(header.Index + 1);
        }

        public SnapshotView GetSnapshot()
        {
            return new SnapshotView(Store);
        }

        public Transaction GetTransaction(UInt256 hash)
        {
            if (MemPool.TryGetValue(hash, out Transaction transaction))
                return transaction;
            return View.GetTransaction(hash);
        }

        private void OnImport(IEnumerable<Block> blocks, bool verify, IActorRef sender, IUntypedActorContext context)
        {
            foreach (Block block in blocks)
            {
                if (block.Index <= Height) continue;
                if (block.Index != Height + 1)
                    throw new InvalidOperationException();
                if (verify && !block.Verify(currentSnapshot))
                    throw new InvalidOperationException();
                Persist(block, context);
                SaveHeaderHashList();
            }
            sender.Tell(new ImportCompleted());
        }

        private void AddUnverifiedBlockToCache(Block block)
        {
            if (!block_cache_unverified.TryGetValue(block.Index, out LinkedList<Block> blocks))
            {
                blocks = new LinkedList<Block>();
                block_cache_unverified.Add(block.Index, blocks);
            }

            blocks.AddLast(block);
        }

        private void OnFillMemoryPool(IEnumerable<Transaction> transactions, IActorRef sender)
        {
            // Invalidate all the transactions in the memory pool, to avoid any failures when adding new transactions.
            MemPool.InvalidateAllTransactions();

            // Add the transactions to the memory pool
            foreach (var tx in transactions)
            {
                if (View.ContainsTransaction(tx.Hash))
                    continue;
                if (!NativeContract.Policy.CheckPolicy(tx, currentSnapshot))
                    continue;
                // First remove the tx if it is unverified in the pool.
                MemPool.TryRemoveUnVerified(tx.Hash, out _);
                // Verify the the transaction
                if (tx.Verify(currentSnapshot, MemPool.SendersFeeMonitor.GetSenderFee(tx.Sender)) != RelayResultReason.Succeed)
                    continue;
                // Add to the memory pool
                MemPool.TryAdd(tx.Hash, tx);
            }
            // Transactions originally in the pool will automatically be reverified based on their priority.

            sender.Tell(new FillCompleted());
        }

        private RelayResultReason OnNewBlock(Block block, IActorRef self, IUntypedActorContext context)
        {
            if (block.Index <= Height)
                return RelayResultReason.AlreadyExists;
            if (block_cache.ContainsKey(block.Hash))
                return RelayResultReason.AlreadyExists;
            if (block.Index - 1 >= header_index.Count)
            {
                AddUnverifiedBlockToCache(block);
                return RelayResultReason.UnableToVerify;
            }
            if (block.Index == header_index.Count)
            {
                if (!block.Verify(currentSnapshot))
                    return RelayResultReason.Invalid;
            }
            else
            {
                if (!block.Hash.Equals(header_index[(int)block.Index]))
                    return RelayResultReason.Invalid;
            }
            if (block.Index == Height + 1)
            {
                Block block_persist = block;
                List<Block> blocksToPersistList = new List<Block>();
                while (true)
                {
                    blocksToPersistList.Add(block_persist);
                    if (block_persist.Index + 1 >= header_index.Count) break;
                    UInt256 hash = header_index[(int)block_persist.Index + 1];
                    if (!block_cache.TryGetValue(hash, out block_persist)) break;
                }

                int blocksPersisted = 0;
                foreach (Block blockToPersist in blocksToPersistList)
                {
                    block_cache_unverified.Remove(blockToPersist.Index);
                    Persist(blockToPersist, context);

                    // 15000 is the default among of seconds per block, while MilliSecondsPerBlock is the current
                    uint extraBlocks = (15000 - MillisecondsPerBlock) / 1000;

                    if (blocksPersisted++ < blocksToPersistList.Count - (2 + Math.Max(0, extraBlocks))) continue;
                    // Empirically calibrated for relaying the most recent 2 blocks persisted with 15s network
                    // Increase in the rate of 1 block per second in configurations with faster blocks

                    if (blockToPersist.Index + 100 >= header_index.Count)
                        localNodeActor.Tell(new LocalNode.RelayDirectly { Inventory = blockToPersist });
                }
                SaveHeaderHashList();

                if (block_cache_unverified.TryGetValue(Height + 1, out LinkedList<Block> unverifiedBlocks))
                {
                    foreach (var unverifiedBlock in unverifiedBlocks)
                        self.Tell(unverifiedBlock, ActorRefs.NoSender);
                    block_cache_unverified.Remove(Height + 1);
                }
            }
            else
            {
                block_cache.Add(block.Hash, block);
                if (block.Index + 100 >= header_index.Count)
                    localNodeActor.Tell(new LocalNode.RelayDirectly { Inventory = block });
                if (block.Index == header_index.Count)
                {
                    header_index.Add(block.Hash);
                    using (SnapshotView snapshot = GetSnapshot())
                    {
                        snapshot.Blocks.Add(block.Hash, block.Header.Trim());
                        snapshot.HeaderHashIndex.GetAndChange().Set(block);
                        SaveHeaderHashList(snapshot);
                        snapshot.Commit();
                    }
                    UpdateCurrentSnapshot();
                }
            }
            return RelayResultReason.Succeed;
        }

        private RelayResultReason OnNewConsensus(ConsensusPayload payload)
        {
            if (!payload.Verify(currentSnapshot)) return RelayResultReason.Invalid;
            consensusServiceActor?.Tell(payload);
            ConsensusRelayCache.Add(payload);
            localNodeActor.Tell(new LocalNode.RelayDirectly { Inventory = payload });
            return RelayResultReason.Succeed;
        }

        private void OnNewHeaders(Header[] headers, IActorRef sender)
        {
            using (SnapshotView snapshot = GetSnapshot())
            {
                foreach (Header header in headers)
                {
                    if (header.Index - 1 >= header_index.Count) break;
                    if (header.Index < header_index.Count) continue;
                    if (!header.Verify(snapshot)) break;
                    header_index.Add(header.Hash);
                    snapshot.Blocks.Add(header.Hash, header.Trim());
                    snapshot.HeaderHashIndex.GetAndChange().Hash = header.Hash;
                    snapshot.HeaderHashIndex.GetAndChange().Index = header.Index;
                }
                SaveHeaderHashList(snapshot);
                snapshot.Commit();
            }
            UpdateCurrentSnapshot();
            taskManagerActor.Tell(new TaskManager.HeaderTaskCompleted(), sender);
        }

        private void OnNewTransaction(Transaction transaction, bool relay, IActorRef self, IActorRef sender)
        {
            RelayResultReason reason;
            if (ContainsTransaction(transaction.Hash))
                reason = RelayResultReason.AlreadyExists;
            else if (!MemPool.CanTransactionFitInPool(transaction))
                reason = RelayResultReason.OutOfMemory;
            else
                reason = transaction.Verify(currentSnapshot, MemPool.SendersFeeMonitor.GetSenderFee(transaction.Sender));

            if (reason == RelayResultReason.Succeed)
            {
                if (!MemPool.TryAdd(transaction.Hash, transaction))
                    reason = RelayResultReason.OutOfMemory;
                else if (relay)
                    localNodeActor.Tell(new LocalNode.RelayDirectly { Inventory = transaction });
            }

            sender.Tell(reason);
        }

        private void OnPersistCompleted(Block block, IUntypedActorContext context)
        {
            block_cache.Remove(block.Hash);
            MemPool.UpdatePoolForBlockPersisted(block, currentSnapshot);
            context.System.EventStream.Publish(new PersistCompleted { Block = block });
        }

        private void Persist(Block block, IUntypedActorContext context)
        {
            using (SnapshotView snapshot = GetSnapshot())
            {
                List<ApplicationExecuted> all_application_executed = new List<ApplicationExecuted>();
                snapshot.PersistingBlock = block;
                if (block.Index > 0)
                {
                    using (ApplicationEngine engine = new ApplicationEngine(TriggerType.System, null, snapshot, 0, true))
                    {
                        engine.LoadScript(onPersistNativeContractScript);
                        if (engine.Execute() != VMState.HALT) throw new InvalidOperationException();
                        ApplicationExecuted application_executed = new ApplicationExecuted(engine);
                        context.System.EventStream.Publish(application_executed);
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

                    using (ApplicationEngine engine = new ApplicationEngine(TriggerType.Application, tx, snapshot.Clone(), tx.SystemFee))
                    {
                        engine.LoadScript(tx.Script);
                        state.VMState = engine.Execute();
                        if (state.VMState == VMState.HALT)
                        {
                            engine.Snapshot.Commit();
                        }
                        ApplicationExecuted application_executed = new ApplicationExecuted(engine);
                        context.System.EventStream.Publish(application_executed);
                        all_application_executed.Add(application_executed);
                    }
                }
                snapshot.BlockHashIndex.GetAndChange().Set(block);
                if (block.Index == header_index.Count)
                {
                    header_index.Add(block.Hash);
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
            UpdateCurrentSnapshot();
            OnPersistCompleted(block, context);
        }

        private void SaveHeaderHashList(SnapshotView snapshot = null)
        {
            if ((header_index.Count - stored_header_count < 2000))
                return;
            bool snapshot_created = snapshot == null;
            if (snapshot_created) snapshot = GetSnapshot();
            try
            {
                while (header_index.Count - stored_header_count >= 2000)
                {
                    snapshot.HeaderHashList.Add(stored_header_count, new HeaderHashList
                    {
                        Hashes = header_index.Skip((int)stored_header_count).Take(2000).ToArray()
                    });
                    stored_header_count += 2000;
                }
                if (snapshot_created) snapshot.Commit();
            }
            finally
            {
                if (snapshot_created) snapshot.Dispose();
            }
        }

        private void UpdateCurrentSnapshot()
        {
            Interlocked.Exchange(ref currentSnapshot, GetSnapshot())?.Dispose();
        }
    }
}
