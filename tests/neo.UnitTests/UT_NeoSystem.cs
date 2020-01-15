using Akka.Actor;
using Autofac;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Persistence;
using NSubstitute;
using Neo.Ledger;
using Neo.UnitTests.Wallets;

namespace Neo.UnitTests
{
    [TestClass]
    public class UT_NeoSystem
    {
        private IStore store;
        private NeoContainer container;

        [TestInitialize]
        public void Setup()
        {
            store = Substitute.For<IStore>();
            container = new NeoContainer();
        }

        [TestMethod]
        public void CreatingActorSytem()
        {
            var actorSystem = container.Container.Resolve<ActorSystem>();
            Assert.IsNotNull(actorSystem);
        }

        [TestMethod]
        public void CreatingMemoryPool()
        {
            var memoryPool = container.ResolveMemoryPool();
            Assert.IsNotNull(memoryPool);
        }

        [TestMethod]
        public void CreatingConsensusService()
        {
            var memoryPool = container.ResolveMemoryPool();
            Assert.IsNotNull(memoryPool);
            Assert.IsNotNull(container.ResolveBlockchain(memoryPool, store));

            var consensusService = container.ResolveConsensusService(store, new MyWallet());
            Assert.IsNotNull(consensusService);
        }

        [TestMethod]
        public void CreatingConsensusServiceActor()
        {
            var memoryPool = container.ResolveMemoryPool();
            Assert.IsNotNull(memoryPool);
            Assert.IsNotNull(container.ResolveBlockchain(memoryPool, store));

            var consensusService = container.ResolveConsensusService(store, new MyWallet());
            Assert.IsNotNull(consensusService);

            var consensusServiceActor = container.ConsensusServiceActor;
            Assert.IsNotNull(consensusServiceActor);
        }

        [TestMethod]
        public void CreatingBlockchain()
        {
            var memoryPool = container.ResolveMemoryPool();
            Assert.IsNotNull(memoryPool);

            Assert.IsNotNull(container.ResolveBlockchain(memoryPool, store));
            container.ResolveConsensusService(store, new MyWallet());

            var blockchain = container.Container.Resolve<Blockchain>();
            Assert.IsNotNull(blockchain);

            blockchain = container.Blockchain;
            Assert.IsNotNull(blockchain);

            var blockchainActor = container.BlockchainActor;
            Assert.IsNotNull(blockchainActor);
        }

        [TestMethod]
        public void CreatingNeoSystem()
        {
            var neoSystem = container.ResolveNeoSystem(store);
            Assert.IsNotNull(neoSystem);
        }

        [TestMethod]
        public void CreatingProtocolHandlerActor()
        {
            var protocolHandler = container.ProtocolHandlerActor;
            Assert.IsNotNull(protocolHandler);
        }

        [TestMethod]
        public void CreatingTaskManager()
        {
            var memoryPool = container.ResolveMemoryPool();
            Assert.IsNotNull(memoryPool);
            Assert.IsNotNull(container.ResolveBlockchain(memoryPool, store));

            var taskManagerActor = container.TaskManagerActor;
            Assert.IsNotNull(taskManagerActor);
        }

        [TestMethod]
        public void CreatingLocalNode()
        {
            var memoryPool = container.ResolveMemoryPool();
            Assert.IsNotNull(memoryPool);
            Assert.IsNotNull(container.ResolveBlockchain(memoryPool, store));

            var localNode = container.LocalNode;
            Assert.IsNotNull(localNode);
        }

        [TestMethod]
        public void CreatingLocalNodeActor()
        {
            var memoryPool = container.ResolveMemoryPool();
            Assert.IsNotNull(memoryPool);
            Assert.IsNotNull(container.ResolveBlockchain(memoryPool, store));

            var localNodeActor = container.LocalNodeActor;
            Assert.IsNotNull(localNodeActor);
        }

        [TestMethod]
        public void CreatingConsensusContext()
        {
            var memoryPool = container.ResolveMemoryPool();
            Assert.IsNotNull(memoryPool);
            Assert.IsNotNull(container.ResolveBlockchain(memoryPool, store));

            var consensusService = container.ResolveConsensusContext(store, new MyWallet());
            Assert.IsNotNull(consensusService);
        }

        [TestMethod]
        public void CreatingContractParametersContext()
        {
            var memoryPool = container.ResolveMemoryPool();
            Assert.IsNotNull(memoryPool);
            Assert.IsNotNull(container.ResolveBlockchain(memoryPool, store));

            var localNode = container.LocalNode;
            Assert.IsNotNull(localNode);

            var contract = container.ContractParametersContext;
            Assert.IsNotNull(contract);
        }

        [TestMethod]
        public void Creating()
        {
            var memoryPool = container.ResolveMemoryPool();
            Assert.IsNotNull(memoryPool);
            Assert.IsNotNull(container.ResolveBlockchain(memoryPool, store));

            var blockchain = container.Container.Resolve<Blockchain>();
            Assert.IsNotNull(blockchain);

            blockchain = container.Blockchain;
            Assert.IsNotNull(blockchain);

            var blockchainActor = container.BlockchainActor;
            Assert.IsNotNull(blockchainActor);

            var taskManagerActor = container.TaskManagerActor;
            Assert.IsNotNull(taskManagerActor);

            var localNode = container.LocalNode;
            Assert.IsNotNull(localNode);

            var localNodeActor = container.LocalNodeActor;
            Assert.IsNotNull(localNodeActor);
        }
    }
}
