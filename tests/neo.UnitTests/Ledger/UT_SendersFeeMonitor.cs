using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using System;
using System.Numerics;

namespace Neo.UnitTests.Ledger
{
    [TestClass]
    public class UT_SendersFeeMonitor
    {
        private TestBlockchain testBlockchain;

        [TestInitialize]
        public void TestSetup()
        {
            testBlockchain = new TestBlockchain();
            testBlockchain.InitializeMockNeoSystem();
        }

        private Transaction CreateTransactionWithFee(Blockchain blockchain, long networkFee, long systemFee)
        {
            Random random = new Random();
            var randomBytes = new byte[16];
            random.NextBytes(randomBytes);
            Mock<Transaction> mock = new Mock<Transaction>();
            mock.Setup(p => p.VerifyForEachBlock(It.IsAny<StoreView>(),
                It.IsAny<BigInteger>())).Returns(RelayResultReason.Succeed);
            mock.Setup(p => p.Verify(blockchain, It.IsAny<StoreView>(),
                It.IsAny<BigInteger>())).Returns(RelayResultReason.Succeed);
            mock.Object.Script = randomBytes;
            mock.Object.Sender = UInt160.Zero;
            mock.Object.NetworkFee = networkFee;
            mock.Object.SystemFee = systemFee;
            mock.Object.Attributes = new TransactionAttribute[0];
            mock.Object.Cosigners = new Cosigner[0];
            mock.Object.Witnesses = new[]
            {
                new Witness
                {
                    InvocationScript = new byte[0],
                    VerificationScript = new byte[0]
                }
            };
            return mock.Object;
        }

        [TestMethod]
        public void TestMemPoolSenderFee()
        {
            Transaction transaction = CreateTransactionWithFee(testBlockchain.Container.Blockchain, 1,
                2);
            SendersFeeMonitor sendersFeeMonitor = new SendersFeeMonitor();
            sendersFeeMonitor.GetSenderFee(transaction.Sender).Should().Be(0);
            sendersFeeMonitor.AddSenderFee(transaction);
            sendersFeeMonitor.GetSenderFee(transaction.Sender).Should().Be(3);
            sendersFeeMonitor.AddSenderFee(transaction);
            sendersFeeMonitor.GetSenderFee(transaction.Sender).Should().Be(6);
            sendersFeeMonitor.RemoveSenderFee(transaction);
            sendersFeeMonitor.GetSenderFee(transaction.Sender).Should().Be(3);
            sendersFeeMonitor.RemoveSenderFee(transaction);
            sendersFeeMonitor.GetSenderFee(transaction.Sender).Should().Be(0);
        }
    }
}
