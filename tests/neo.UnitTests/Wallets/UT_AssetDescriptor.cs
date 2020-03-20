using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Persistence;
using Neo.SmartContract.Native;
using System;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.SmartContract;

namespace Neo.UnitTests.Wallets
{
    [TestClass]
    public class UT_AssetDescriptor
    {
        private TestBlockchain testBlockchain;
        private Blockchain blockchain;

        [TestInitialize]
        public void TestSetup()
        {
            testBlockchain = new TestBlockchain();
            testBlockchain.InitializeMockNeoSystem();
            blockchain = testBlockchain.Container.Blockchain;

            var applicationEngine = new ApplicationEngine(blockchain, TriggerType.Application, new Block(),
                blockchain.GetSnapshot(), 0L, testMode: true);
            NativeContract.Policy.Initialize(applicationEngine);
        }

        [TestMethod]
        public void TestConstructorWithNonexistAssetId()
        {
            Action action = () =>
            {
                var descriptor = new Neo.Wallets.AssetDescriptor(blockchain,
                    UInt160.Parse("01ff00ff00ff00ff00ff00ff00ff00ff00ff00a4"));
            };
            action.Should().Throw<ArgumentException>();
        }

        [TestMethod]
        public void Check_GAS()
        {
            var descriptor = new Neo.Wallets.AssetDescriptor(blockchain,
                NativeContract.GAS.Hash);
            descriptor.AssetId.Should().Be(NativeContract.GAS.Hash);
            descriptor.AssetName.Should().Be("GAS");
            descriptor.ToString().Should().Be("GAS");
            descriptor.Decimals.Should().Be(8);
        }

        [TestMethod]
        public void Check_NEO()
        {
            var descriptor = new Neo.Wallets.AssetDescriptor(blockchain,
                NativeContract.NEO.Hash);
            descriptor.AssetId.Should().Be(NativeContract.NEO.Hash);
            descriptor.AssetName.Should().Be("NEO");
            descriptor.ToString().Should().Be("NEO");
            descriptor.Decimals.Should().Be(0);
        }
    }
}
