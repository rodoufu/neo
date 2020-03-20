using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Neo.IO;
using Neo.IO.Caching;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.VM.Types;
using System;

namespace Neo.UnitTests.SmartContract
{
    [TestClass]
    public class UT_ApplicationEngine
    {
        private string message = null;
        private StackItem item = null;

        private TestBlockchain testBlockchain;

        [TestInitialize]
        public void TestSetup()
        {
            testBlockchain = new TestBlockchain();
            testBlockchain.InitializeMockNeoSystem();
        }

        [TestMethod]
        public void TestLog()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            var snapshot = blockchain.GetSnapshot();
            var engine = new ApplicationEngine(blockchain, TriggerType.Application, null, snapshot, 0,
                true);
            ApplicationEngine.Log += Test_Log1;
            string logMessage = "TestMessage";

            engine.SendLog(UInt160.Zero, logMessage);
            message.Should().Be(logMessage);

            ApplicationEngine.Log += Test_Log2;
            engine.SendLog(UInt160.Zero, logMessage);
            message.Should().Be(null);

            message = logMessage;
            ApplicationEngine.Log -= Test_Log1;
            engine.SendLog(UInt160.Zero, logMessage);
            message.Should().Be(null);

            ApplicationEngine.Log -= Test_Log2;
            engine.SendLog(UInt160.Zero, logMessage);
            message.Should().Be(null);
        }

        [TestMethod]
        public void TestNotify()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            var snapshot = blockchain.GetSnapshot();
            var engine = new ApplicationEngine(blockchain, TriggerType.Application, null, snapshot, 0,
                true);
            ApplicationEngine.Notify += Test_Notify1;
            StackItem notifyItem = "TestItem";

            engine.SendNotification(UInt160.Zero, notifyItem);
            item.Should().Be(notifyItem);

            ApplicationEngine.Notify += Test_Notify2;
            engine.SendNotification(UInt160.Zero, notifyItem);
            item.Should().Be(null);

            item = notifyItem;
            ApplicationEngine.Notify -= Test_Notify1;
            engine.SendNotification(UInt160.Zero, notifyItem);
            item.Should().Be(null);

            ApplicationEngine.Notify -= Test_Notify2;
            engine.SendNotification(UInt160.Zero, notifyItem);
            item.Should().Be(null);
        }

        [TestMethod]
        public void TestDisposable()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            var snapshot = blockchain.GetSnapshot();
            var m = new Mock<IDisposable>();
            var engine = new ApplicationEngine(blockchain, TriggerType.Application, null, snapshot, 0,
                true);
            engine.AddDisposable(m.Object).Should().Be(m.Object);
            Action action = () => engine.Dispose();
            action.Should().NotThrow();
        }

        private void Test_Log1(object sender, LogEventArgs e)
        {
            message = e.Message;
        }

        private void Test_Log2(object sender, LogEventArgs e)
        {
            message = null;
        }

        private void Test_Notify1(object sender, NotifyEventArgs e)
        {
            item = e.State;
        }

        private void Test_Notify2(object sender, NotifyEventArgs e)
        {
            item = null;
        }

        [TestMethod]
        public void TestCreateDummyBlock()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            var snapshot = blockchain.GetSnapshot();
            byte[] SyscallSystemRuntimeCheckWitnessHash = new byte[] { 0x68, 0xf8, 0x27, 0xec, 0x8c };
            ApplicationEngine.Run(blockchain, SyscallSystemRuntimeCheckWitnessHash, snapshot);
            snapshot.PersistingBlock.Version.Should().Be(0);
            snapshot.PersistingBlock.PrevHash.Should().Be(Blockchain.GenesisBlock.Hash);
            snapshot.PersistingBlock.MerkleRoot.Should().Be(new UInt256());
        }

        [TestMethod]
        public void TestOnSysCall()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            InteropDescriptor descriptor = new InteropDescriptor("System.Blockchain.GetHeight",
                Blockchain_GetHeight, 0_00000400, TriggerType.Application, CallFlags.None);
            TestApplicationEngine engine = new TestApplicationEngine(blockchain, TriggerType.Application,
                null, null, 0);
            byte[] SyscallSystemRuntimeCheckWitnessHash = new byte[] { 0x68, 0xf8, 0x27, 0xec, 0x8c };
            engine.LoadScript(SyscallSystemRuntimeCheckWitnessHash);
            engine.GetOnSysCall(descriptor.Hash).Should().BeFalse();

            var snapshot = blockchain.GetSnapshot();
            engine = new TestApplicationEngine(blockchain, TriggerType.Application, null, snapshot, 0,
                true);
            engine.LoadScript(SyscallSystemRuntimeCheckWitnessHash);
            engine.GetOnSysCall(descriptor.Hash).Should().BeTrue();
        }

        private static bool Blockchain_GetHeight(ApplicationEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.Snapshot.Height);
            return true;
        }
    }

    public class TestApplicationEngine : ApplicationEngine
    {
        public TestApplicationEngine(Blockchain blockchain, TriggerType trigger, IVerifiable container,
            StoreView snapshot, long gas, bool testMode = false) : base(blockchain, trigger, container, snapshot, gas,
            testMode)
        {
        }

        public bool GetOnSysCall(uint method)
        {
            return OnSysCall(method);
        }
    }

    public class TestMetaDataCache<T> : MetaDataCache<T> where T : class, ICloneable<T>, ISerializable, new()
    {
        public TestMetaDataCache()
            : base(null)
        {
        }

        protected override void AddInternal(T item)
        {
        }

        protected override T TryGetInternal()
        {
            return new T();
        }

        protected override void UpdateInternal(T item)
        {
        }
    }
}
