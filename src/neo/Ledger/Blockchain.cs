using Akka.Actor;
using Neo.Cryptography.ECC;
using Neo.IO.Caching;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.VM;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Neo.Ledger
{
    public sealed partial class Blockchain
    {
        public partial class ApplicationExecuted { }
        public class PersistCompleted { public Block Block; }
        public class Import { public IEnumerable<Block> Blocks; }
        public class ImportCompleted { }
        public class FillMemoryPool { public IEnumerable<Transaction> Transactions; }
        public class FillCompleted { }
        private class ParallelVerified { public Transaction Transaction; public bool ShouldRelay; public RelayResultReason VerifyResult; }

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

        private void AddUnverifiedBlockToCache(Block block)
        {
            if (!block_cache_unverified.TryGetValue(block.Index, out LinkedList<Block> blocks))
            {
                blocks = new LinkedList<Block>();
                block_cache_unverified.Add(block.Index, blocks);
            }

            blocks.AddLast(block);
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
