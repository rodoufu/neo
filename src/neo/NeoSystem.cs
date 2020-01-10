using Akka.Actor;
using Neo.Consensus;
using Neo.Network.P2P;
using Neo.Persistence;
using Neo.Plugins;
using Neo.Wallets;
using System;

namespace Neo
{
    public class NeoSystem : IDisposable
    {
        public ActorSystem ActorSystem => neoContainer.ActorSystem;

        private readonly IStore store;
        private ChannelsConfig start_message = null;
        private bool suspend = false;

        private readonly NeoContainer neoContainer;
        private IActorRef localNodeActor => neoContainer.LocalNodeActor;
        private IActorRef blockchainActor => neoContainer.BlockchainActor;
        private IActorRef Consensus => neoContainer.ConsensusServiceActor;

        internal NeoSystem(NeoContainer neoContainer, IStore store)
        {
            this.neoContainer = neoContainer;
            Plugin.LoadPlugins(this);
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
            Consensus.Tell(new ConsensusService.Start {IgnoreRecoveryLogs = ignoreRecoveryLogs}, blockchainActor);
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
