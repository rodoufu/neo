using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Persistence;
using NSubstitute;
using FluentAssertions;

namespace Neo.UnitTests
{
    [TestClass]
    public class UT_NeoSystem
    {
        private NeoSystem neoSystem;

        [TestInitialize]
        public void Setup()
        {
            var store = Substitute.For<Store>();
            // TODO FIXME this tests
//            neoSystem = new NeoSystem(store);
            neoSystem = TestBlockchain.InitializeMockNeoSystem();
        }

//        [TestMethod]
//        public void TestGetBlockchain() => neoSystem.Blockchain.Should().NotBeNull();

//        [TestMethod]
//        public void TestGetLocalNode() => neoSystem.LocalNode.Should().NotBeNull();

//        [TestMethod]
//        public void TestGetTaskManager() => neoSystem.TaskManager.Should().NotBeNull();

        [TestMethod]
        public void TestGetConsensus() => neoSystem.Consensus.Should().BeNull();

        [TestMethod]
        public void TestGetRpcServer() => neoSystem.RpcServer.Should().BeNull();

        [TestMethod]
        public void Creating()
        {
            Assert.IsNotNull(neoSystem);
            Assert.IsNotNull(neoSystem.ActorSystem);
            // TODO FIXME this tests
//            Assert.IsNotNull(neoSystem.Blockchain);
//            Assert.IsNotNull(neoSystem.LocalNode);

            // TODO FIXME this tests
//            neoSystem.Blockchain.Tell("oi", neoSystem.LocalNode);
        }
    }
}
