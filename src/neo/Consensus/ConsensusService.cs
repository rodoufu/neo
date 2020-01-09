using Akka.Actor;
using Akka.Configuration;
using Neo.Cryptography;
using Neo.IO;
using Neo.IO.Actors;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract.Native;
using Neo.Wallets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autofac;

namespace Neo.Consensus
{
    public sealed partial class ConsensusService
    {
        public class Start { public bool IgnoreRecoveryLogs; }
        public class SetViewNumber { public byte ViewNumber; }
        internal class Timer { public uint Height; public byte ViewNumber; }

        private readonly NeoContainer neoContainer;
        private readonly ConsensusContext context;
        private IActorRef localNode => neoContainer.LocalNodeActor;
        private LocalNode theLocalNode => neoContainer.LocalNode;
        private IActorRef taskManager => neoContainer.TaskManagerActor;
        private Blockchain blockchain => neoContainer.Blockchain;
        private ICancelable timer_token;
        private DateTime block_received_time;
        private bool started = false;

        /// <summary>
        /// This will record the information from last scheduled timer
        /// </summary>
        private DateTime clock_started = TimeProvider.Current.UtcNow;
        private TimeSpan expected_delay = TimeSpan.Zero;

        /// <summary>
        /// This will be cleared every block (so it will not grow out of control, but is used to prevent repeatedly
        /// responding to the same message.
        /// </summary>
        private readonly HashSet<UInt256> knownHashes = new HashSet<UInt256>();
        /// <summary>
        /// This variable is only true during OnRecoveryMessageReceived
        /// </summary>
        private bool isRecovering = false;

        public ConsensusService(NeoContainer neoContainer, IStore store, Wallet wallet)
        {
            this.neoContainer = neoContainer;
            this.context = new ConsensusContext(wallet, store);
        }

        private void CheckCommits()
        {
            if (context.CommitPayloads.Count(p => p?.ConsensusMessage.ViewNumber == context.ViewNumber) >= context.M && context.TransactionHashes.All(p => context.Transactions.ContainsKey(p)))
            {
                Block block = context.CreateBlock();
                Log($"relay block: height={block.Index} hash={block.Hash} tx={block.Transactions.Length}");
                localNode.Tell(new LocalNode.Relay { Inventory = block });
            }
        }

        private void Log(string message, LogLevel level = LogLevel.Info)
        {
            Utility.Log(nameof(ConsensusService), level, message);
        }

        private void RequestRecovery()
        {
            if (context.Block.Index == blockchain.HeaderHeight + 1)
                localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakeRecoveryRequest() });
        }

        private static ConsensusService CreateConsensusService(Wallet wallet, NeoContainer neoContainer)
        {
            var consensusService = neoContainer.Container.Resolve<ConsensusService>();
            consensusService.context.Wallet = wallet;
            return consensusService;
        }

    }

}
