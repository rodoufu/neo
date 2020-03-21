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
        public override bool Verify(Blockchain blockchain, StoreView snapshot)
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
        public override bool Verify(Blockchain blockchain, StoreView snapshot)
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
        private Transaction txSample = Blockchain.GenesisBlock.Transactions[0];
        private NeoSystem system;
        private TestBlockchain testBlockchain;
        private Blockchain blockchain;

        [TestInitialize]
        public void TestSetup()
        {
            testBlockchain = new TestBlockchain();
            testBlockchain.InitializeMockNeoSystem();
            system = testBlockchain.NeoSystem;
            blockchain = testBlockchain.Container.Blockchain;

            /*snapshot = _context.Snapshot;
            var applicationEngine = new ApplicationEngine(blockchain, TriggerType.Application, new Block(),
                snapshot, 0L, testMode: true);
            NativeContract.Policy.Initialize(applicationEngine);*/
        }

        [TestMethod]
        public void TestContainsBlock()
        {
            testBlockchain.MemoryPool.TryAdd(txSample.Hash, txSample);
            blockchain.ContainsBlock(UInt256.Zero).Should().BeFalse();
        }

        [TestMethod]
        public void TestContainsTransaction()
        {
            testBlockchain.MemoryPool.TryAdd(txSample.Hash, txSample);
            blockchain.ContainsTransaction(UInt256.Zero).Should().BeFalse();
            blockchain.ContainsTransaction(txSample.Hash).Should().BeTrue();
        }

        [TestMethod]
        public void TestGetCurrentBlockHash()
        {
            testBlockchain.MemoryPool.TryAdd(txSample.Hash, txSample);
            blockchain.CurrentBlockHash.Should().Be(UInt256.Parse(
                "0x2b8a21dfaf989dc1a5f2694517aefdbda1dd340f3cf177187d73e038a58ad2bb"));
        }

        [TestMethod]
        public void TestGetCurrentHeaderHash()
        {
            testBlockchain.MemoryPool.TryAdd(txSample.Hash, txSample);
            blockchain.CurrentHeaderHash.Should().Be(UInt256.Parse(
                "0x2b8a21dfaf989dc1a5f2694517aefdbda1dd340f3cf177187d73e038a58ad2bb"));
        }

        [TestMethod]
        public void TestGetBlock()
        {
            testBlockchain.MemoryPool.TryAdd(txSample.Hash, txSample);
            blockchain.GetBlock(UInt256.Zero).Should().BeNull();
        }

        [TestMethod]
        public void TestGetBlockHash()
        {
            testBlockchain.MemoryPool.TryAdd(txSample.Hash, txSample);
            blockchain.GetBlockHash(0).Should().Be(UInt256.Parse(
                "0x2b8a21dfaf989dc1a5f2694517aefdbda1dd340f3cf177187d73e038a58ad2bb"));
            blockchain.GetBlockHash(10).Should().BeNull();
        }

        [TestMethod]
        public void TestGetTransaction()
        {
            testBlockchain.MemoryPool.TryAdd(txSample.Hash, txSample);
            blockchain.GetTransaction(UInt256.Zero).Should().BeNull();
            blockchain.GetTransaction(txSample.Hash).Should().NotBeNull();
        }

        [TestMethod]
        public void TestValidTransaction()
        {
            testBlockchain.MemoryPool.TryAdd(txSample.Hash, txSample);
            var senderProbe = CreateTestProbe();
            var snapshot = blockchain.GetSnapshot();
            var walletA = TestUtils.GenerateTestWallet();

            using (var unlockA = walletA.Unlock("123"))
            {
                var acc = walletA.CreateAccount();

                // Fake balance

                var key = NativeContract.GAS.CreateStorageKey(20, acc.ScriptHash);
                var entry = snapshot.Storages.GetAndChange(key, () => new StorageItem
                {
                    Value = new Nep5AccountState().ToByteArray()
                });

                entry.Value = new Nep5AccountState()
                {
                    Balance = 100_000_000 * NativeContract.GAS.Factor
                }
                .ToByteArray();

                snapshot.Commit();

                typeof(Blockchain)
                    .GetMethod("UpdateCurrentSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(blockchain, null);

                // Make transaction

                var tx = CreateValidTx(walletA, acc.ScriptHash, 0, testBlockchain);

                var blockchainActor = testBlockchain.Container.BlockchainActor;
                senderProbe.Send(blockchainActor, tx);
                senderProbe.ExpectMsg(RelayResultReason.Succeed);

                senderProbe.Send(blockchainActor, tx);
                senderProbe.ExpectMsg(RelayResultReason.AlreadyExists);
            }
        }

        private Transaction CreateValidTx(NEP6Wallet wallet, UInt160 account, uint nonce, TestBlockchain testBlockchain)
        {
            var tx = wallet.MakeTransaction(blockchain, new TransferOutput[]
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

            var data = testBlockchain.Container.ResolveContractParametersContext(tx);
            Assert.IsTrue(wallet.Sign(data));
            Assert.IsTrue(data.Completed);

            tx.Witnesses = data.GetWitnesses();

            return tx;
        }
    }
}
