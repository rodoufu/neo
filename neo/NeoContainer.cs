using System;
using System.Net;
using System.Security;
using Akka.Actor;
using Akka.DI.AutoFac;
using Akka.DI.Core;
using Autofac;
using Neo.Consensus;
using Neo.Cryptography;
using Neo.IO.Json;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Persistence;
using Neo.Wallets;
using Neo.Wallets.NEP6;
using Neo.Wallets.SQLite;

namespace Neo
{
    /// <summary>
    /// NEO dependency injection container.
    /// </summary>
    public class NeoContainer
    {
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

        public static void NeoInstance(Store store, Wallet wallet)
        {
            Instance = new NeoContainer(store, wallet);
        }

        /// <summary>
        /// Container builder.
        /// Used to create register the dependencies.
        /// </summary>
        public readonly ContainerBuilder Builder;

        private IContainer _container;

        private NeoContainer(Store store, Wallet wallet)
        {
            Builder = new ContainerBuilder();

            Builder.RegisterInstance(this).SingleInstance();
            Builder.RegisterInstance(store);
            Builder.RegisterInstance(wallet);

            Builder.RegisterType<Blockchain>().SingleInstance().AsSelf();
            Builder.RegisterType<LocalNode>().SingleInstance().AsSelf();
            Builder.RegisterType<TaskManager>().AsSelf();
            Builder.RegisterType<ConsensusService>().AsSelf();
            Builder.RegisterType<NeoSystem>().AsSelf();

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

            RegisterActors();
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

        public Blockchain Blockchain => Container.Resolve<Blockchain>();

        public Props RemoteNodeProps(object connection, IPEndPoint remote, IPEndPoint local)
        {
            var remoteNode = Container.Resolve<RemoteNode>();
            remoteNode.SetConnection(connection);
            remoteNode.Remote = remote;
            remoteNode.Local = local;
            return Props.Create(() => remoteNode).WithMailbox("remote-node-mailbox");
        }

    }
}
