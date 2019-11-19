using System;
using System.Net;
using Akka.Actor;
using Akka.DI.AutoFac;
using Akka.DI.Core;
using Autofac;
using Autofac.Builder;
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
        // TODO remove
        private static NeoContainer _instance;

        public static NeoContainer Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new NullReferenceException($"{typeof(NeoContainer).Name} not initialized yet");
                }

                return _instance;
            }
            private set => _instance = value;
        }

        public static void NeoInstance()
        {
            Instance = new NeoContainer();
        }

        /// <summary>
        /// Container builder.
        /// Used to create register the dependencies.
        /// </summary>
        public readonly ContainerBuilder Builder;

        private IContainer _container;

        public NeoContainer()
        {
            Builder = new ContainerBuilder();
            Builder.RegisterInstance(this).SingleInstance();
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
            });

            Register<Blockchain, BlockchainActor>(Builder, "blockchain-mailbox", true);
            Register<LocalNode, LocalNodeActor>(Builder, string.Empty, true);
            Register<TaskManager, TaskManagerActor>(Builder, "task-manager-mailbox");
            Register<ConsensusService, ConsensusServiceActor>(Builder, "consensus-service-mailbox");
            Register<ProtocolHandler, ProtocolHandlerProps>(Builder, "protocol-handler-mailbox");

            Builder.RegisterType<LocalNode>().SingleInstance().AsSelf();
            Builder.RegisterType<TaskManager>().AsSelf();

            Builder.Register((c, p) =>
                new ConsensusService(
                    c.Resolve<LocalNodeActor>(),
                    c.Resolve<TaskManagerActor>(),
                    c.Resolve<LocalNode>(),
                    c.Resolve<Blockchain>(),
                    p.Named<Store>("store"),
                    p.Named<Wallet>("wallet")
                )).As<ConsensusService>();

            Builder.Register((c, p) =>
                new MemoryPool(
                    c.Resolve<BlockchainActor>(),
                    c.Resolve<LocalNodeActor>(),
                    p.Named<int>("capacity")
                )).As<MemoryPool>();

            Builder.Register((c, p) =>
                new Blockchain(
                    c.Resolve<LocalNodeActor>(),
                    c.Resolve<ConsensusServiceActor>(),
                    c.Resolve<TaskManagerActor>(),
                    p.Named<MemoryPool>("memoryPool"),
                    p.Named<Store>("store")
                )).SingleInstance().As<Blockchain>();

            Builder.Register((c, p) =>
                new RpcServer(
                    c.Resolve<Blockchain>(),
                    c.Resolve<BlockchainActor>(),
                    c.Resolve<LocalNode>(),
                    p.Named<Wallet>("wallet"),
                    p.Named<long>("maxGasInvoke")
                )).As<RpcServer>();

            Builder.Register((c, p) =>
                new NeoSystem(
                    c.Resolve<LocalNodeActor>(),
                    c.Resolve<ConsensusServiceActor>(),
                    c.Resolve<RpcServer>(),
                    c.Resolve<BlockchainActor>(),
                    p.Named<Store>("store")
                )).As<NeoSystem>();
        }

        /// <summary>
        /// The dependency injection container.
        /// It should only be used after all the dependencies are registered.
        /// </summary>
        public IContainer Container => _container ?? Builder.Build();

        private void Register<T1, T2>(ContainerBuilder builder, string mailBox, bool singleInstance = false)
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

        // TODO remove
        public Blockchain Blockchain => Container.Resolve<Blockchain>();

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

        public Blockchain ResolveBlockchain(MemoryPool memoryPool, Store store) =>
            Container.Resolve<Blockchain>(
                new NamedParameter("memoryPool", memoryPool),
                new NamedParameter("store", store)
            );

        public MemoryPool ResolveMemoryPool(int capacity = 100) =>
            Container.Resolve<MemoryPool>(new NamedParameter("capacity", capacity));
    }
}