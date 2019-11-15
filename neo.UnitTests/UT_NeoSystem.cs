using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Persistence;
using NSubstitute;

namespace Neo.UnitTests
{
    [TestClass]
    public class UT_NeoSystem
    {
        private NeoSystem neoSystem;

        [TestInitialize]
        public void TestSetup()
        {
            var store = Substitute.For<Store>();
            // TODO FIXME this tests
//            neoSystem = new NeoSystem(store);
        }

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
