using System;
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

            RegisterActors();
            Builder.RegisterInstance(this).SingleInstance();

            Builder.RegisterType<LocalNode>().SingleInstance().AsSelf();
            Builder.RegisterType<TaskManager>().AsSelf();
            Builder.RegisterType<ConsensusService>().AsSelf();

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

            Builder.Register(c =>
                    ActorSystem.Create(nameof(NeoSystem),
                        $"akka {{ log-dead-letters = off }}" +
                        $"blockchain-mailbox {{ mailbox-type: \"{typeof(BlockchainMailbox).AssemblyQualifiedName}\" }}" +
                        $"task-manager-mailbox {{ mailbox-type: \"{typeof(TaskManagerMailbox).AssemblyQualifiedName}\" }}" +
                        $"remote-node-mailbox {{ mailbox-type: \"{typeof(RemoteNodeMailbox).AssemblyQualifiedName}\" }}" +
                        $"protocol-handler-mailbox {{ mailbox-type: \"{typeof(ProtocolHandlerMailbox).AssemblyQualifiedName}\" }}" +
                        $"consensus-service-mailbox {{ mailbox-type: \"{typeof(ConsensusServiceMailbox).AssemblyQualifiedName}\" }}")
                )
                .SingleInstance().OnActivated(h =>
                {
                    h.Instance.UseAutofac(Container);
                    var propsResolver = new AutoFacDependencyResolver(Container, h.Instance);
                });
        }

        /// <summary>
        /// The dependency injection container.
        /// It should only be used after all the dependencies are registered.
        /// </summary>
        public IContainer Container => _container ?? Builder.Build();

        private void Register<T1, T2>(ContainerBuilder builder, string mailBox)
            where T1 : ActorBase
        {
            builder.Register(c =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                var props = actorSystem.DI().Props<T1>();
                if (!string.IsNullOrEmpty(mailBox))
                {
                    props = props.WithMailbox(mailBox);
                }

                return actorSystem.ActorOf(props);
            }).As<T2>();
        }

        private void RegisterActors()
        {
            Builder.Register(c =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                var props = actorSystem.DI().Props<Blockchain>().WithMailbox("blockchain-mailbox");
                return actorSystem.ActorOf(props);
            }).As<BlockchainActor>();

            Builder.Register(c =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                var props = actorSystem.DI().Props<LocalNode>();
                return actorSystem.ActorOf(props);
            }).As<LocalNodeActor>();

            Builder.Register(c =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                var props = actorSystem.DI().Props<TaskManager>().WithMailbox("task-manager-mailbox");
                return actorSystem.ActorOf(props);
            }).As<TaskManagerActor>();

            Builder.Register(c =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                var props = actorSystem.DI().Props<ConsensusService>().WithMailbox("consensus-service-mailbox");
                return actorSystem.ActorOf(props);
            }).As<ConsensusServiceActor>();

            Builder.Register(c =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                return actorSystem.DI().Props<ProtocolHandler>().WithMailbox("protocol-handler-mailbox");
            }).As<ProtocolHandlerProps>();
        }

        // TODO remove
        public Blockchain Blockchain => Container.Resolve<Blockchain>();

        public Props RemoteNodeProps(object connection, IPEndPoint remote, IPEndPoint local)
        {
            var remoteNode = Container.Resolve<RemoteNode>();
            remoteNode.SetConnection(connection);
            remoteNode.Remote = remote;
            remoteNode.Local = local;
            return Props.Create(() => remoteNode).WithMailbox("remote-node-mailbox");
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
