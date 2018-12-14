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
            neoSystem = new NeoSystem(store);
        }

        [TestMethod]
        public void Craeating()
        {
            Assert.IsNotNull(neoSystem);
            Assert.IsNotNull(neoSystem.ActorSystem);
            Assert.IsNotNull(neoSystem.Blockchain);
            Assert.IsNotNull(neoSystem.LocalNode);

            neoSystem.Blockchain.Tell("oi", neoSystem.LocalNode);
        }
    }
}