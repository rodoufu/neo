using System;
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
using System.Threading;
using Neo.Network.P2P.Payloads;
using Neo.SmartContract;

namespace Neo
{
    /// <summary>
    /// NEO dependency injection container.
    /// </summary>
    public class NeoContainer
    {
        private const string BlockchainMailbox = "blockchain-mailbox";
        private const string TaskManagerMailbox = "task-manager-mailbox";
        private const string ProtocolHandlerMailbox = "protocol-handler-mailbox";
        private const string RemoteNodeMailbox = "remote-node-mailbox";
        private const string ConsensusServiceMailbox = "consensus-service-mailbox";

        private static NeoContainer _instance;

        /// <summary>
        /// Workaround for accessing the Blockchain.
        /// Its very hacky and we should remove it.
        /// </summary>
        public static NeoContainer Instance
        {
            get
            {
                Console.WriteLine("=>static NeoContainer Instance");
                while (_instance == null)
                {
                    Thread.Sleep(200);
                }

                return _instance;
            }
        }

        private IContainer container;
        private bool blockchainActorResolved;

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

        private string ActorAddress(string mailbox, Type theType) =>
            $"{mailbox} {{ mailbox-type: \"{theType.AssemblyQualifiedName}\" }}";

        /// <summary>
        /// Create the container and also register the components.
        /// </summary>
        public NeoContainer()
        {
            Builder = new ContainerBuilder();

            Builder.RegisterInstance(this).SingleInstance().OnActivated(h => _instance = h.Instance)
                .As<NeoContainer>();

            Builder.Register(c => ActorSystem.Create(nameof(NeoSystem),
                $"akka {{ log-dead-letters = off }}" +
                ActorAddress(BlockchainMailbox, typeof(BlockchainMailbox)) +
                ActorAddress(TaskManagerMailbox, typeof(TaskManagerMailbox)) +
                ActorAddress(RemoteNodeMailbox, typeof(RemoteNodeMailbox)) +
                ActorAddress(ProtocolHandlerMailbox, typeof(ProtocolHandlerMailbox)) +
                ActorAddress(ConsensusServiceMailbox, typeof(ConsensusServiceMailbox))
            )).OnActivating(h =>
            {
                h.Instance.UseAutofac(Container);
                var propsResolver = new AutoFacDependencyResolver(Container, h.Instance);
            }).SingleInstance().As<ActorSystem>();

            Builder.Register((c, p) => new NeoSystem(
                c.Resolve<NeoContainer>(),
                p.Named<IStore>("store")
            )).As<NeoSystem>();

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
                    actorSystem.DI().Props<Blockchain.BlockchainActor>().WithMailbox(BlockchainMailbox)
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
                    actorSystem.DI().Props<TaskManager>().WithMailbox(TaskManagerMailbox)
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
                        .WithMailbox(ConsensusServiceMailbox)
                );
            }).SingleInstance().Named<IActorRef>(typeof(ConsensusService).Name);

            Builder.Register((c, p) => new ConsensusContext(
                c.Resolve<NeoContainer>(),
                p.Named<Wallet>("wallet"),
                p.Named<IStore>("store")
            )).As<ConsensusContext>();

            Builder.RegisterType<ProtocolHandler>();
            Builder.Register((c, p) =>
            {
                var actorSystem = c.Resolve<ActorSystem>();
                return actorSystem.ActorOf(
                    actorSystem.DI().Props<ProtocolHandler>()
                        .WithMailbox(ProtocolHandlerMailbox)
                );
            }).SingleInstance().Named<IActorRef>(typeof(ProtocolHandler).Name);
            Builder.Register((c, p) =>
                    Props.Create(() => c.Resolve<ProtocolHandler>(p)).WithMailbox(ProtocolHandlerMailbox))
                .Named<Props>(typeof(ProtocolHandler).Name);

            Builder.Register((c, p) => new RemoteNode(
                p.Named<object>("connection"),
                p.Named<IPEndPoint>("remote"),
                p.Named<IPEndPoint>("local"),
                c.Resolve<NeoContainer>(),
                c.Resolve<LocalNode>()
            )).As<RemoteNode>();
            Builder.Register((c, p) =>
                    Props.Create(() => c.Resolve<RemoteNode>(p)).WithMailbox(RemoteNodeMailbox))
                .Named<Props>(typeof(RemoteNode).Name);

            Builder.Register((c, p) => new ContractParametersContext(
                c.Resolve<NeoContainer>(),
                p.Named<IVerifiable>("verifiable")
            )).As<ContractParametersContext>();
        }

        public MemoryPool ResolveMemoryPool(int capacity = 100) =>
            Container.Resolve<MemoryPool>(new NamedParameter("capacity", capacity));

        private Blockchain ResolveBlockchain(MemoryPool memoryPool, IStore store) => Container.Resolve<Blockchain>(
            new NamedParameter("memoryPool", memoryPool),
            new NamedParameter("store", store)
        );

        /// <summary>
        /// Should be used only after the NeoContainer is created.
        /// </summary>
        internal Blockchain Blockchain
        {
            get
            {
                Console.WriteLine("=>internal Blockchain Blockchain");
                while (!blockchainActorResolved)
                {
                    Console.WriteLine("=>internal Blockchain Blockchain - lock");
                    Thread.Sleep(200);
                }

                return Container.Resolve<Blockchain>();
            }
        }

        public IActorRef ResolveBlockchainActor(MemoryPool memoryPool, IStore store)
        {
            ResolveBlockchain(memoryPool, store);
            return BlockchainActor;
        }

        public IActorRef BlockchainActor
        {
            get
            {
                var blockchainActor = Container.ResolveNamed<IActorRef>(typeof(Blockchain).Name);
                blockchainActorResolved = true;
                return blockchainActor;
            }
        }

        public Props ResolveRemoteNodeProps(object connection, IPEndPoint remote, IPEndPoint local) =>
            Container.ResolveNamed<Props>(
                typeof(RemoteNode).Name,
                new NamedParameter("connection", connection),
                new NamedParameter("remote", remote),
                new NamedParameter("local", local));

        public Props ProtocolHandlerProps => Container.ResolveNamed<Props>(typeof(ProtocolHandler).Name);

        public NeoSystem ResolveNeoSystem(IStore store) =>
            Container.Resolve<NeoSystem>(new NamedParameter("store", store));

        internal LocalNode LocalNode => Container.Resolve<LocalNode>();

        public IActorRef LocalNodeActor => Container.ResolveNamed<IActorRef>(typeof(LocalNode).Name);

        public ConsensusService ResolveConsensusService(IStore store, Wallet wallet) =>
            Container.Resolve<ConsensusService>(
                new NamedParameter("store", store),
                new NamedParameter("wallet", wallet)
            );

        public IActorRef ConsensusServiceActor => Container.ResolveNamed<IActorRef>(typeof(ConsensusService).Name);

        internal ConsensusContext ResolveConsensusContext(Wallet wallet, IStore store) =>
            Container.Resolve<ConsensusContext>(
                new NamedParameter("store", store),
                new NamedParameter("wallet", wallet)
            );

        public IActorRef TaskManagerActor => Container.ResolveNamed<IActorRef>(typeof(TaskManager).Name);

        public ActorSystem ActorSystem => Container.Resolve<ActorSystem>();

        public IActorRef ProtocolHandlerActor => Container.ResolveNamed<IActorRef>(typeof(ProtocolHandler).Name);

        public ContractParametersContext ResolveContractParametersContext(IVerifiable verifiable = null) =>
            Container.Resolve<ContractParametersContext>(
                new NamedParameter("verifiable", verifiable)
            );
    }
}
