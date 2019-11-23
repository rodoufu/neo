using System.Net;
using Akka.Actor;
using Akka.DI.AutoFac;
using Akka.DI.Core;
using Autofac;
using Neo.Consensus;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.RPC;
using Neo.Persistence;
using Neo.Wallets;

namespace Neo
{
    /// <summary>
    /// NEO dependency injection container.
    /// </summary>
    public class NeoContainer
    {
        /// <summary>
        /// Workaround for accessing the blockchain.
        /// </summary>
        public static NeoContainer Instance { get; private set; }

        /// <summary>
        /// Should be used only after the NeoContainer is created.
        /// </summary>
        public Blockchain Blockchain => Container.Resolve<Blockchain>();

        private IContainer _container;
        /// <summary>
        /// The dependency injection container.
        /// It should only be used after all the dependencies are registered.
        /// </summary>
        public IContainer Container
        {
            get
            {
                if (_container == null)
                {
                    _container = Builder.Build();
                }

                return _container;
            }
        }

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
            Builder.Register(c => ActorSystem.Create(nameof(NeoSystem),
                $"akka {{ log-dead-letters = off }}" +
                $"blockchain-mailbox {{ mailbox-type: \"{typeof(BlockchainMailbox).AssemblyQualifiedName}\" }}" +
                $"task-manager-mailbox {{ mailbox-type: \"{typeof(TaskManagerMailbox).AssemblyQualifiedName}\" }}" +
                $"remote-node-mailbox {{ mailbox-type: \"{typeof(RemoteNodeMailbox).AssemblyQualifiedName}\" }}" +
                $"protocol-handler-mailbox {{ mailbox-type: \"{typeof(ProtocolHandlerMailbox).AssemblyQualifiedName}\" }}" +
                $"consensus-service-mailbox {{ mailbox-type: \"{typeof(ConsensusServiceMailbox).AssemblyQualifiedName}\" }}")
            ).SingleInstance().OnActivated(h =>
            {
                h.Instance.UseAutofac(Container);
                var propsResolver = new AutoFacDependencyResolver(Container, h.Instance);
            }).As<ActorSystem>();

            Builder.Register((c, p) => new MemoryPool(
                c.Resolve<NeoContainer>(),
                p.Named<int>("capacity")
            )).As<MemoryPool>();

            // Blockchain
            Builder.Register((c, p) => new Blockchain(
                c.Resolve<NeoContainer>(),
                p.Named<MemoryPool>("memoryPool"),
                p.Named<Store>("store")
            )).SingleInstance().As<Blockchain>();
            Register<Blockchain, BlockchainActor>(Builder, "blockchain-mailbox", true);

            // LocalNode
            Builder.RegisterType<LocalNode>().SingleInstance().AsSelf();
            Register<LocalNode, LocalNodeActor>(Builder, string.Empty, true);

            // TaskManager
            Builder.RegisterType<TaskManager>().AsSelf();
            Register<TaskManager, TaskManagerActor>(Builder, "task-manager-mailbox");

            // ConsensusService
            Builder.Register((c, p) => new ConsensusService(
                c.Resolve<LocalNodeActor>(),
                c.Resolve<TaskManagerActor>(),
                c.Resolve<LocalNode>(),
                c.Resolve<Blockchain>(),
                p.Named<Store>("store"),
                p.Named<Wallet>("wallet")
            )).As<ConsensusService>();
            Register<ConsensusService, ConsensusServiceActor>(Builder, "consensus-service-mailbox");

            // ProtocolHandler
            Builder.RegisterType<ProtocolHandler>().AsSelf();
            Register<ProtocolHandler, ProtocolHandlerProps>(Builder, "protocol-handler-mailbox");

            Builder.Register((c, p) => new RpcServer(
                c.Resolve<Blockchain>(),
                c.Resolve<BlockchainActor>(),
                c.Resolve<LocalNode>(),
                p.Named<Wallet>("wallet"),
                p.Named<long>("maxGasInvoke")
            )).As<RpcServer>();

            Builder.Register((c, p) => new NeoSystem(
                c.Resolve<LocalNodeActor>(),
                c.Resolve<ConsensusServiceActor>(),
                c.Resolve<RpcServer>(),
                c.Resolve<BlockchainActor>(),
                p.Named<Store>("store")
            )).SingleInstance().As<NeoSystem>();
        }

        /// <summary>
        /// Register the actor and the correspondent class.
        /// </summary>
        /// <param name="builder">Container builder.</param>
        /// <param name="mailBox">Mailbox for the user.</param>
        /// <param name="singleInstance">Indicate if the instance should be singleton.</param>
        /// <typeparam name="T1">Class for the actor.</typeparam>
        /// <typeparam name="T2">The actor.</typeparam>
        private static void Register<T1, T2>(ContainerBuilder builder, string mailBox = null,
            bool singleInstance = false)
            where T1 : ActorBase
        {
            var registration = builder.Register(c =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                var props = actorSystem.DI().Props<T1>();
                if (!string.IsNullOrEmpty(mailBox))
                {
                    props = props.WithMailbox(mailBox);
                }

                return actorSystem.ActorOf(props);
            });
            if (singleInstance)
            {
                registration.SingleInstance();
            }

            registration.As<T2>();
        }

        public RemoteNodeProps ResolveNodeProps(object connection, IPEndPoint remote, IPEndPoint local)
        {
            var remoteNode = Container.Resolve<RemoteNode>();
            remoteNode.SetConnection(connection);
            remoteNode.Remote = remote;
            remoteNode.Local = local;
            return (RemoteNodeProps) Props.Create(() => remoteNode).WithMailbox("remote-node-mailbox");
        }

        public NeoSystem ResolveNeoSystem(Store store) =>
            Container.Resolve<NeoSystem>(new NamedParameter("store", store));

        public RpcServer ResolveRpcServer(Wallet wallet = null, long maxGasInvoke = default) =>
            Container.Resolve<RpcServer>(
                new NamedParameter("wallet", wallet),
                new NamedParameter("maxGasInvoke", maxGasInvoke)
            );

        public Blockchain ResolveBlockchain(MemoryPool memoryPool, Store store) => Container.Resolve<Blockchain>(
            new NamedParameter("memoryPool", memoryPool),
            new NamedParameter("store", store)
        );

        public MemoryPool ResolveMemoryPool(int capacity = 100) =>
            Container.Resolve<MemoryPool>(new NamedParameter("capacity", capacity));

        public LocalNodeActor ResolveLocalNodeActor() => Container.Resolve<LocalNodeActor>();

        public ConsensusService ResolveConsensusService(Store store, Wallet wallet) =>
            Container.Resolve<ConsensusService>(
                new NamedParameter("store", store),
                new NamedParameter("wallet", wallet)
            );

        public ConsensusServiceActor ResolveConsensusServiceActor() => Container.Resolve<ConsensusServiceActor>();

        public TaskManagerActor ResolveTaskManagerActor() => Container.Resolve<TaskManagerActor>();

        public BlockchainActor ResolveBlockchainActor() => Container.Resolve<BlockchainActor>();
    }
}
