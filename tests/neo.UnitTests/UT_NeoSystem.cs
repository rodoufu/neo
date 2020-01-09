using Akka.Actor;
using Autofac;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Network.P2P;
using Neo.Persistence;
using NSubstitute;
using FluentAssertions;
using Neo.Ledger;
using Neo.UnitTests.Wallets;

namespace Neo.UnitTests
{
    [TestClass]
    public class UT_NeoSystem
    {
        private IStore store;
        private NeoSystem neoSystem;
        private NeoContainer container;

        [TestInitialize]
        public void Setup()
        {
            store = Substitute.For<IStore>();
            container = new NeoContainer();
//            container.Builder.RegisterInstance(store).As<Store>();

//            neoSystem = container.ResolveNeoSystem(store);
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

            /*
            var taskManagerActor = container.TaskManagerActor;
            Assert.IsNotNull(taskManagerActor);

            var localNode = container.LocalNode;
            Assert.IsNotNull(localNode);

            var localNodeActor = container.LocalNodeActor;
            Assert.IsNotNull(localNodeActor);
            */


//            Assert.IsNotNull(container.ResolveBlockchainActor(memoryPool, store));
//            Assert.IsNotNull(container.BlockchainActor);
//            Assert.IsNotNull(container.Blockchain);
//            Assert.IsNotNull(neoSystem);
//            Assert.IsNotNull(neoSystem.ActorSystem);
//            Assert.IsNotNull(container.Blockchain);
//            Assert.IsNotNull(container.ResolveLocalNode());

//            container.ResolveBlockchainActor().Tell("oi", container.ResolveLocalNodeActor());
        }

////        [TestMethod]
////        public void TestGetBlockchain() => container.Blockchain.Should().NotBeNull();
//
//        [TestMethod]
//        public void TestGetLocalNode() => container.ResolveLocalNode().Should().NotBeNull();
//
//        [TestMethod]
//        public void TestGetConsensus() => neoSystem.Consensus.Should().BeNull();
//
//        [TestMethod]
//        public void TestGetRpcServer() => neoSystem.RpcServer.Should().BeNull();
    }
}
