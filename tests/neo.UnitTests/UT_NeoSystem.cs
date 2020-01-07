using Akka.Actor;
using Autofac;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Persistence;
using NSubstitute;
using FluentAssertions;
using Neo.Ledger;
using Neo.Network.P2P;

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
        public void Creating()
        {
            var memoryPool = container.ResolveMemoryPool();
            Assert.IsNotNull(memoryPool);
            Assert.IsNotNull(container.ResolveBlockchain(memoryPool, store));

            var blockchain = container.Container.Resolve<Blockchain>();
            Assert.IsNotNull(blockchain);

            var taskManager = container.TaskManagerActor;
            Assert.IsNotNull(taskManager);

            var localNode = container.LocalNodeActor;
            Assert.IsNotNull(localNode);
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
