using Akka.Actor;
using Akka.DI.AutoFac;
using Akka.DI.Core;
using Autofac;
using Neo.Consensus;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Persistence;
using Neo.Wallets;
using System.Net;

namespace Neo
{
    /// <summary>
    /// NEO dependency injection container.
    /// </summary>
    public class NeoContainer
    {
        /// <summary>
        /// Workaround for accessing the blockchain.
        /// Its very hacky and we should remove it.
        /// </summary>
        public static NeoContainer Instance { get; private set; }

        private IContainer container;
        private object actorSystemLock = new object();

        /// <summary>
        /// The dependency injection container.
        /// It should only be used after all the dependencies are registered.
        /// </summary>
        public IContainer Container => container ??= Builder.Build();

        /// <summary>
        /// Container builder.
        /// Used to create register the dependencies.
        /// </summary>
        public readonly ContainerBuilder Builder;

        /// <summary>
        /// Create the container and also register the components.
        /// </summary>
        public NeoContainer()
        {
            Builder = new ContainerBuilder();

            Builder.RegisterInstance(this).SingleInstance().OnActivated(h => Instance = h.Instance).As<NeoContainer>();

            Builder.Register(c =>
            {
                lock (actorSystemLock)
                {
                    return ActorSystem.Create(nameof(NeoSystem),
                        $"akka {{ log-dead-letters = off }}" +
                        $"blockchain-mailbox {{ mailbox-type: \"{typeof(Blockchain.BlockchainMailbox).AssemblyQualifiedName}\" }}" +
                        $"task-manager-mailbox {{ mailbox-type: \"{typeof(TaskManagerMailbox).AssemblyQualifiedName}\" }}" +
                        $"remote-node-mailbox {{ mailbox-type: \"{typeof(RemoteNodeMailbox).AssemblyQualifiedName}\" }}" +
                        $"protocol-handler-mailbox {{ mailbox-type: \"{typeof(ProtocolHandlerMailbox).AssemblyQualifiedName}\" }}" +
                        $"consensus-service-mailbox {{ mailbox-type: \"{typeof(ConsensusServiceMailbox).AssemblyQualifiedName}\" }}");
                }
            }).OnActivating(h =>
            {
                h.Instance.UseAutofac(Container);
                var propsResolver = new AutoFacDependencyResolver(Container, h.Instance);
            }).SingleInstance().As<ActorSystem>();

            Builder.Register((c, p) => new MemoryPool(
                c.Resolve<NeoContainer>(),
                p.Named<int>("capacity")
            )).As<MemoryPool>();

            // Blockchain
            Builder.Register((c, p) => new Blockchain(
                c.Resolve<NeoContainer>(),
                p.Named<MemoryPool>("memoryPool"),
                p.Named<IStore>("store")
            )).SingleInstance().As<Blockchain>();
            Builder.RegisterType<Blockchain.BlockchainActor>().SingleInstance();
            Builder.Register((c, p) =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                return actorSystem.ActorOf(
                    actorSystem.DI().Props<Blockchain.BlockchainActor>().WithMailbox("blockchain-mailbox")
                );
            }).SingleInstance().Named<IActorRef>(typeof(Blockchain).Name);

            Builder.RegisterType<LocalNode>().SingleInstance();
            Builder.RegisterType<LocalNode.LocalNodeActor>().SingleInstance();
            Builder.Register((c, p) =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                return actorSystem.ActorOf(
                    actorSystem.DI().Props<LocalNode.LocalNodeActor>()
                );
            }).SingleInstance().Named<IActorRef>(typeof(LocalNode).Name);

            Builder.RegisterType<TaskManager>().SingleInstance();
            Builder.Register((c, p) =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                return actorSystem.ActorOf(
                    actorSystem.DI().Props<TaskManager>().WithMailbox("task-manager-mailbox")
                );
            }).SingleInstance().Named<IActorRef>(typeof(TaskManager).Name);

            Builder.Register((c, p) => new ConsensusService(
                c.Resolve<NeoContainer>(),
                p.Named<IStore>("store"),
                p.Named<Wallet>("wallet")
            )).As<ConsensusService>();
            Builder.Register((c, p) =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                return actorSystem.ActorOf(
                    actorSystem.DI().Props<ConsensusService.ConsensusServiceActor>()
                        .WithMailbox("consensus-service-mailbox")
                );
            }).SingleInstance().Named<IActorRef>(typeof(ConsensusService).Name);

            Builder.Register((c, p) => new ConsensusContext(
                c.Resolve<NeoContainer>(),
                p.Named<Wallet>("wallet"),
                p.Named<IStore>("store")
            )).As<ConsensusContext>();

//            // ProtocolHandler
//            Builder.RegisterType<ProtocolHandler>().AsSelf();
//            Register<ProtocolHandler, ProtocolHandlerProps>(Builder, "protocol-handler-mailbox");
//
//            Builder.Register((c, p) => new RpcServer(
//                c.Resolve<Blockchain>(),
//                c.Resolve<BlockchainActor>(),
//                c.Resolve<LocalNode>(),
//                p.Named<Wallet>("wallet"),
//                p.Named<long>("maxGasInvoke")
//            )).As<RpcServer>();
//
//            Builder.Register((c, p) => new NeoSystem(
//                c.Resolve<LocalNodeActor>(),
//                c.Resolve<ConsensusServiceActor>(),
//                c.Resolve<RpcServer>(),
//                c.Resolve<BlockchainActor>(),
//                p.Named<Store>("store")
//            )).SingleInstance().As<NeoSystem>();
        }

        public MemoryPool ResolveMemoryPool(int capacity = 100) =>
            Container.Resolve<MemoryPool>(new NamedParameter("capacity", capacity));

        public Blockchain ResolveBlockchain(MemoryPool memoryPool, IStore store) => Container.Resolve<Blockchain>(
            new NamedParameter("memoryPool", memoryPool),
            new NamedParameter("store", store)
        );

        /// <summary>
        /// Should be used only after the NeoContainer is created.
        /// </summary>
        internal Blockchain Blockchain => Container.Resolve<Blockchain>();

        public IActorRef ResolveBlockchainActor(MemoryPool memoryPool, IStore store) =>
            Container.ResolveNamed<IActorRef>(
                typeof(Blockchain).Name,
                new NamedParameter("memoryPool", memoryPool),
                new NamedParameter("store", store)
            );

        public IActorRef BlockchainActor => Container.ResolveNamed<IActorRef>(typeof(Blockchain).Name);

        public RemoteNodeProps ResolveNodeProps(object connection, IPEndPoint remote, IPEndPoint local)
        {
//        public RemoteNode(object connection, IPEndPoint remote, IPEndPoint local, NeoContainer neoContainer)
            var remoteNode = Container.Resolve<RemoteNode>();
            // TODO register and fix me
//            remoteNode.SetConnection(connection);
//            remoteNode.Remote = remote;
//            remoteNode.Local = local;
            return (RemoteNodeProps) Props.Create(() => remoteNode).WithMailbox("remote-node-mailbox");
        }

        public NeoSystem ResolveNeoSystem(IStore store) =>
            Container.Resolve<NeoSystem>(new NamedParameter("store", store));

        public NeoSystem NeoSystem => Container.Resolve<NeoSystem>();

        internal LocalNode LocalNode => Container.Resolve<LocalNode>();

        public IActorRef LocalNodeActor => Container.ResolveNamed<IActorRef>(typeof(LocalNode).Name);

        public ConsensusService ResolveConsensusService(IStore store, Wallet wallet) =>
            Container.Resolve<ConsensusService>(
                new NamedParameter("store", store),
                new NamedParameter("wallet", wallet)
            );

        public IActorRef ConsensusServiceActor => Container.ResolveNamed<IActorRef>(typeof(ConsensusService).Name);

        internal ConsensusContext ResolveConsensusContext(IStore store, Wallet wallet) =>
            Container.Resolve<ConsensusContext>(
                new NamedParameter("store", store),
                new NamedParameter("wallet", wallet)
            );

        public IActorRef TaskManagerActor => Container.ResolveNamed<IActorRef>(typeof(TaskManager).Name);

        public ActorSystem ActorSystem => Container.Resolve<ActorSystem>();
    }
}
