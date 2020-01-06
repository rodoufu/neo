using Akka.TestKit.Xunit2;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.SmartContract.Native.Tokens;
using Neo.Wallets;
using Neo.Wallets.NEP6;
using System.Linq;
using System.Reflection;

namespace Neo.UnitTests.Ledger
{
    internal class TestBlock : Block
    {
        public override bool Verify(StoreView snapshot)
        {
            return true;
        }

        public static TestBlock Cast(Block input)
        {
            return input.ToArray().AsSerializable<TestBlock>();
        }
    }

    internal class TestHeader : Header
    {
        public override bool Verify(StoreView snapshot)
        {
            return true;
        }

        public static TestHeader Cast(Header input)
        {
            return input.ToArray().AsSerializable<TestHeader>();
        }
    }

    [TestClass]
    public class UT_Blockchain : TestKit
    {
        private NeoSystem system;
        Transaction txSample = Blockchain.GenesisBlock.Transactions[0];

        [TestInitialize]
        public void Initialize()
        {
            system = TestBlockchain.TheNeoSystem;
            // TODO @rodoufu fix this
//            Blockchain.Singleton.MemPool.TryAdd(txSample.Hash, txSample);
        }

        [TestMethod]
        public void TestContainsBlock()
        {
            // TODO @rodoufu fix this
//            Blockchain.Singleton.ContainsBlock(UInt256.Zero).Should().BeFalse();
        }

        [TestMethod]
        public void TestContainsTransaction()
        {
            // TODO @rodoufu fix this
//            Blockchain.Singleton.ContainsTransaction(UInt256.Zero).Should().BeFalse();
//            Blockchain.Singleton.ContainsTransaction(txSample.Hash).Should().BeTrue();
        }

        [TestMethod]
        public void TestGetCurrentBlockHash()
        {
            // TODO @rodoufu fix this
//            Blockchain.Singleton.CurrentBlockHash.Should().Be(UInt256.Parse("0x2b8a21dfaf989dc1a5f2694517aefdbda1dd340f3cf177187d73e038a58ad2bb"));
        }

        [TestMethod]
        public void TestGetCurrentHeaderHash()
        {
            // TODO @rodoufu fix this
//            Blockchain.Singleton.CurrentHeaderHash.Should().Be(UInt256.Parse("0x2b8a21dfaf989dc1a5f2694517aefdbda1dd340f3cf177187d73e038a58ad2bb"));
        }

        [TestMethod]
        public void TestGetBlock()
        {
            // TODO @rodoufu fix this
//            Blockchain.Singleton.GetBlock(UInt256.Zero).Should().BeNull();
        }

        [TestMethod]
        public void TestGetBlockHash()
        {
            // TODO @rodoufu fix this
//            Blockchain.Singleton.GetBlockHash(0).Should().Be(UInt256.Parse("0x2b8a21dfaf989dc1a5f2694517aefdbda1dd340f3cf177187d73e038a58ad2bb"));
//            Blockchain.Singleton.GetBlockHash(10).Should().BeNull();
        }

        [TestMethod]
        public void TestGetTransaction()
        {
            // TODO @rodoufu fix this
//            Blockchain.Singleton.GetTransaction(UInt256.Zero).Should().BeNull();
//            Blockchain.Singleton.GetTransaction(txSample.Hash).Should().NotBeNull();
        }

        [TestMethod]
        public void TestValidTransaction()
        {
            // TODO @rodoufu fix this
//            var senderProbe = CreateTestProbe();
//            var snapshot = Blockchain.Singleton.GetSnapshot();
//            var walletA = TestUtils.GenerateTestWallet();
//
//            using (var unlockA = walletA.Unlock("123"))
//            {
//                var acc = walletA.CreateAccount();
//
//                // Fake balance
//
//                var key = NativeContract.GAS.CreateStorageKey(20, acc.ScriptHash);
//                var entry = snapshot.Storages.GetAndChange(key, () => new StorageItem
//                {
//                    Value = new Nep5AccountState().ToByteArray()
//                });
//
//                entry.Value = new Nep5AccountState()
//                {
//                    Balance = 100_000_000 * NativeContract.GAS.Factor
//                }
//                .ToByteArray();
//
//                snapshot.Commit();
//
//                typeof(Blockchain)
//                    .GetMethod("UpdateCurrentSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
//                    .Invoke(Blockchain.Singleton, null);
//
//                // Make transaction
//
//                var tx = CreateValidTx(walletA, acc.ScriptHash, 0);
//
//                senderProbe.Send(system.Blockchain, tx);
//                senderProbe.ExpectMsg(RelayResultReason.Succeed);
//
//                senderProbe.Send(system.Blockchain, tx);
//                senderProbe.ExpectMsg(RelayResultReason.AlreadyExists);
//            }
        }

        private Transaction CreateValidTx(NEP6Wallet wallet, UInt160 account, uint nonce)
        {
            var tx = wallet.MakeTransaction(new TransferOutput[]
                {
                    new TransferOutput()
                    {
                            AssetId = NativeContract.GAS.Hash,
                            ScriptHash = account,
                            Value = new BigDecimal(1,8)
                    }
                },
                account);

            tx.Nonce = nonce;

            var data = new ContractParametersContext(tx);
            Assert.IsTrue(wallet.Sign(data));
            Assert.IsTrue(data.Completed);

            tx.Witnesses = data.GetWitnesses();

            return tx;
        }
    }
}
