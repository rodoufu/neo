using Akka.Actor;
using Neo.Consensus;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.RPC;
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
        public RpcServer RpcServer { get; private set; }

        private readonly Store store;
        private readonly LocalNodeActor localNodeActor;
        private readonly BlockchainActorRef _blockchainActorRef;
        private ChannelsConfig start_message = null;
        private bool suspend = false;

        internal NeoSystem(LocalNodeActor localNodeActor, ConsensusServiceActor consensusServiceActor,
            RpcServer rpcServer, BlockchainActorRef blockchainActorRef, Store store)
        {
            this.localNodeActor = localNodeActor;
            this._blockchainActorRef = blockchainActorRef;
            RpcServer = rpcServer;
            Consensus = consensusServiceActor;
            this.store = store;
            Plugin.LoadPlugins(this);
            Plugin.NotifyPluginsLoadedAfterSystemConstructed();
        }

        public void Dispose()
        {
            foreach (var p in Plugin.Plugins)
                p.Dispose();
            RpcServer?.Dispose();
            EnsureStoped(localNodeActor);
            // Dispose will call ActorSystem.Terminate()
            ActorSystem.Dispose();
            ActorSystem.WhenTerminated.Wait();
        }

        public void EnsureStoped(IActorRef actor)
        {
            Inbox inbox = Inbox.Create(ActorSystem);
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

        public void StartConsensus(Wallet wallet, Store consensus_store = null, bool ignoreRecoveryLogs = false)
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

        public void StartRpc(IPAddress bindAddress, int port, Wallet wallet = null, string sslCert = null, string password = null,
            string[] trustedAuthorities = null, long maxGasInvoke = default)
        {
            RpcServer.Wallet = wallet;
            RpcServer.MaxGasInvoke = maxGasInvoke;
            RpcServer.Start(bindAddress, port, sslCert, password, trustedAuthorities);
        }

        internal void SuspendNodeStartup()
        {
            suspend = true;
        }
    }
}
