using Akka.Actor;
using Neo.Consensus;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Persistence;
using Neo.Plugins;
using Neo.Wallets;
using System;
using System.Net;
using Akka.DI.AutoFac;
using Autofac;
using SQLitePCL;

namespace Neo
{
    public class NeoSystem : IDisposable
    {
        public ActorSystem ActorSystem { get; }
        public ConsensusServiceActor Consensus { get; private set; }

        private readonly IStore store;
        private readonly LocalNodeActor localNodeActor;
        private readonly BlockchainActorRef _blockchainActorRef;
        private ChannelsConfig start_message = null;
        private bool suspend = false;

        internal NeoSystem(LocalNodeActor localNodeActor, ConsensusServiceActor consensusServiceActor,
            BlockchainActorRef blockchainActorRef, IStore store)
        {
            Plugin.LoadPlugins(this);
            this.localNodeActor = localNodeActor;
            this._blockchainActorRef = blockchainActorRef;
            Consensus = consensusServiceActor;
            this.store = store;
            foreach (var plugin in Plugin.Plugins)
                plugin.OnPluginsLoaded();
        }

        public void Dispose()
        {
            foreach (var p in Plugin.Plugins)
                p.Dispose();
            EnsureStoped(localNodeActor);
            // Dispose will call ActorSystem.Terminate()
            ActorSystem.Dispose();
            ActorSystem.WhenTerminated.Wait();
            store.Dispose();
        }

        public void EnsureStoped(IActorRef actor)
        {
            using Inbox inbox = Inbox.Create(ActorSystem);
            inbox.Watch(actor);
            ActorSystem.Stop(actor);
            inbox.Receive(TimeSpan.FromMinutes(5));
        }

        internal void ResumeNodeStartup()
        {
            suspend = false;
            if (start_message != null)
            {
                localNodeActor.Tell(start_message);
                start_message = null;
            }
        }

        public void StartConsensus(Wallet wallet, IStore consensus_store = null, bool ignoreRecoveryLogs = false)
        {
            Consensus.Tell(new ConsensusService.Start { IgnoreRecoveryLogs = ignoreRecoveryLogs }, _blockchainActorRef);
        }

        public void StartNode(ChannelsConfig config)
        {
            start_message = config;

            if (!suspend)
            {
                localNodeActor.Tell(start_message);
                start_message = null;
            }
        }

        internal void SuspendNodeStartup()
        {
            suspend = true;
        }
    }
}
