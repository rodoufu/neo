using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.UnitTests.Extensions;
using Neo.VM;
using Neo.VM.Types;
using System;
using System.Linq;
using System.Numerics;

namespace Neo.UnitTests.SmartContract.Native.Tokens
{
    [TestClass]
    public class UT_GasToken
    {
        private TestBlockchain testBlockchain;

        [TestInitialize]
        public void TestSetup()
        {
            testBlockchain = new TestBlockchain();
            testBlockchain.InitializeMockNeoSystem();
        }

        [TestMethod]
        public void Check_Name() => NativeContract.GAS.Name(testBlockchain.Container.Blockchain)
            .Should().Be("GAS");

        [TestMethod]
        public void Check_Symbol() => NativeContract.GAS.Symbol(testBlockchain.Container.Blockchain)
            .Should().Be("gas");

        [TestMethod]
        public void Check_Decimals() => NativeContract.GAS.Decimals(testBlockchain.Container.Blockchain)
            .Should().Be(8);

        [TestMethod]
        public void Check_SupportedStandards() => NativeContract.GAS
            .SupportedStandards(testBlockchain.Container.Blockchain).Should()
            .BeEquivalentTo(new string[] { "NEP-5", "NEP-10" });

        [TestMethod]
        public void Check_BalanceOfTransferAndBurn()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            var snapshot = blockchain.GetSnapshot();
            snapshot.PersistingBlock = new Block() { Index = 1000 };

            byte[] from = Contract.CreateMultiSigRedeemScript(Blockchain.StandbyValidators.Length / 2 + 1,
                Blockchain.StandbyValidators).ToScriptHash().ToArray();

            byte[] to = new byte[20];

            var keyCount = snapshot.Storages.GetChangeSet().Count();

            NativeContract.NEO.Initialize(new ApplicationEngine(blockchain, TriggerType.Application, null,
                snapshot, 0));
            var supply = NativeContract.GAS.TotalSupply(snapshot);
            supply.Should().Be(3000000000000000);

            // Check unclaim

            var unclaim = UT_NeoToken.Check_UnclaimedGas(blockchain, snapshot, from);
            unclaim.Value.Should().Be(new BigInteger(600000000000));
            unclaim.State.Should().BeTrue();

            // Transfer

            NativeContract.NEO.Transfer(blockchain, snapshot, from, to, BigInteger.Zero, true).Should().BeTrue();
            NativeContract.NEO.BalanceOf(blockchain, snapshot, from).Should().Be(100_000_000);
            NativeContract.NEO.BalanceOf(blockchain, snapshot, to).Should().Be(0);

            NativeContract.GAS.BalanceOf(blockchain, snapshot, from).Should().Be(3000600000000000);
            NativeContract.GAS.BalanceOf(blockchain, snapshot, to).Should().Be(0);

            // Check unclaim

            unclaim = UT_NeoToken.Check_UnclaimedGas(blockchain, snapshot, from);
            unclaim.Value.Should().Be(new BigInteger(0));
            unclaim.State.Should().BeTrue();

            supply = NativeContract.GAS.TotalSupply(snapshot);
            supply.Should().Be(3000600000000000);

            snapshot.Storages.GetChangeSet().Count().Should().Be(keyCount + 3); // Gas

            // Transfer

            keyCount = snapshot.Storages.GetChangeSet().Count();

            NativeContract.GAS.Transfer(blockchain, snapshot, from, to, 3000600000000000, false)
                .Should().BeFalse(); // Not signed
            NativeContract.GAS.Transfer(blockchain, snapshot, from, to, 3000600000000001, true)
                .Should().BeFalse(); // More than balance
            NativeContract.GAS.Transfer(blockchain, snapshot, from, to, 3000600000000000, true)
                .Should().BeTrue(); // All balance

            // Balance of

            NativeContract.GAS.BalanceOf(blockchain, snapshot, to).Should().Be(3000600000000000);
            NativeContract.GAS.BalanceOf(blockchain, snapshot, from).Should().Be(0);

            snapshot.Storages.GetChangeSet().Count().Should().Be(keyCount + 1); // All

            // Burn

            var engine = new ApplicationEngine(blockchain, TriggerType.Application, null, snapshot, 0);
            keyCount = snapshot.Storages.GetChangeSet().Count();

            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                NativeContract.GAS.Burn(engine, new UInt160(to), BigInteger.MinusOne));

            // Burn more than expected

            Assert.ThrowsException<InvalidOperationException>(() =>
                NativeContract.GAS.Burn(engine, new UInt160(to), new BigInteger(3000600000000001)));

            // Real burn

            NativeContract.GAS.Burn(engine, new UInt160(to), new BigInteger(1));

            NativeContract.GAS.BalanceOf(blockchain, snapshot, to).Should().Be(3000599999999999);

            keyCount.Should().Be(snapshot.Storages.GetChangeSet().Count());

            // Burn all

            NativeContract.GAS.Burn(engine, new UInt160(to), new BigInteger(3000599999999999));

            (keyCount - 1).Should().Be(snapshot.Storages.GetChangeSet().Count());

            // Bad inputs

            NativeContract.GAS.Transfer(blockchain, snapshot, from, to, BigInteger.MinusOne, true)
                .Should().BeFalse();
            NativeContract.GAS.Transfer(blockchain, snapshot, new byte[19], to, BigInteger.One, false)
                .Should().BeFalse();
            NativeContract.GAS.Transfer(blockchain, snapshot, from, new byte[19], BigInteger.One, false)
                .Should().BeFalse();
        }

        [TestMethod]
        public void Check_BadScript()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            var engine = new ApplicationEngine(blockchain, TriggerType.Application, null,
                blockchain.GetSnapshot(), 0);

            var script = new ScriptBuilder();
            script.Emit(OpCode.NOP);
            engine.LoadScript(script.ToArray());

            NativeContract.GAS.Invoke(engine).Should().BeFalse();
        }

        [TestMethod]
        public void TestGetSysFeeAmount1()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            using (ApplicationEngine engine = NativeContract.GAS.TestCall(blockchain,
                "getSysFeeAmount", 2u))
            {
                engine.ResultStack.Peek().GetBigInteger().Should().Be(new BigInteger(0));
                engine.ResultStack.Peek().GetType().Should().Be(typeof(Integer));
            }

            using (ApplicationEngine engine = NativeContract.GAS.TestCall(blockchain,
                "getSysFeeAmount", 0u))
            {
                engine.ResultStack.Peek().GetBigInteger().Should().Be(new BigInteger(0));
            }
        }

        [TestMethod]
        public void TestGetSysFeeAmount2()
        {
            var snapshot = testBlockchain.Container.Blockchain.GetSnapshot();
            NativeContract.GAS.GetSysFeeAmount(snapshot, 0).Should().Be(new BigInteger(0));
            NativeContract.GAS.GetSysFeeAmount(snapshot, 1).Should().Be(new BigInteger(0));

            byte[] key = BitConverter.GetBytes(1);
            StorageKey storageKey = new StorageKey
            {
                Id = NativeContract.GAS.Id,
                Key = new byte[sizeof(byte) + key.Length]
            };
            storageKey.Key[0] = 15;
            key.CopyTo(storageKey.Key.AsSpan(1));

            BigInteger sys_fee = new BigInteger(10);
            snapshot.Storages.Add(storageKey, new StorageItem
            {
                Value = sys_fee.ToByteArrayStandard(),
                IsConstant = true
            });

            NativeContract.GAS.GetSysFeeAmount(snapshot, 1).Should().Be(sys_fee);
        }
    }
}
