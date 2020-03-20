using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Cryptography;
using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using Neo.SmartContract;
using Neo.SmartContract.Enumerators;
using Neo.SmartContract.Iterators;
using Neo.SmartContract.Manifest;
using Neo.VM;
using Neo.VM.Types;
using Neo.Wallets;
using System.Linq;
using VMArray = Neo.VM.Types.Array;

namespace Neo.UnitTests.SmartContract
{
    public partial class UT_InteropService
    {
        [TestMethod]
        public void TestCheckSig()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain, true);
            IVerifiable iv = engine.ScriptContainer;
            byte[] message = iv.GetHashData();
            byte[] privateKey = { 0x01,0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01};
            KeyPair keyPair = new KeyPair(privateKey);
            ECPoint pubkey = keyPair.PublicKey;
            byte[] signature = Crypto.Sign(message, privateKey, pubkey.EncodePoint(false).Skip(1).ToArray());
            engine.CurrentContext.EvaluationStack.Push(signature);
            engine.CurrentContext.EvaluationStack.Push(pubkey.EncodePoint(false));
            engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
            InteropService.Invoke(engine, InteropService.Crypto.ECDsaVerify).Should().BeTrue();
            engine.CurrentContext.EvaluationStack.Pop().ToBoolean().Should().BeTrue();

            engine.CurrentContext.EvaluationStack.Push(signature);
            engine.CurrentContext.EvaluationStack.Push(new byte[70]);
            engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
            InteropService.Invoke(engine, InteropService.Crypto.ECDsaVerify).Should().BeTrue();
            engine.CurrentContext.EvaluationStack.Pop().ToBoolean().Should().BeFalse();
        }

        [TestMethod]
        public void TestCrypto_CheckMultiSig()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain, true);
            IVerifiable iv = engine.ScriptContainer;
            byte[] message = iv.GetHashData();

            byte[] privkey1 = { 0x01,0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01};
            KeyPair key1 = new KeyPair(privkey1);
            ECPoint pubkey1 = key1.PublicKey;
            byte[] signature1 = Crypto.Sign(message, privkey1, pubkey1.EncodePoint(false).Skip(1).ToArray());

            byte[] privkey2 = { 0x01,0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x02};
            KeyPair key2 = new KeyPair(privkey2);
            ECPoint pubkey2 = key2.PublicKey;
            byte[] signature2 = Crypto.Sign(message, privkey2, pubkey2.EncodePoint(false).Skip(1).ToArray());

            var pubkeys = new VMArray
            {
                pubkey1.EncodePoint(false),
                pubkey2.EncodePoint(false)
            };
            var signatures = new VMArray
            {
                signature1,
                signature2
            };
            engine.CurrentContext.EvaluationStack.Push(signatures);
            engine.CurrentContext.EvaluationStack.Push(pubkeys);
            engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
            InteropService.Invoke(engine, InteropService.Crypto.ECDsaCheckMultiSig).Should().BeTrue();
            engine.CurrentContext.EvaluationStack.Pop().ToBoolean().Should().BeTrue();

            pubkeys = new VMArray();
            engine.CurrentContext.EvaluationStack.Push(signatures);
            engine.CurrentContext.EvaluationStack.Push(pubkeys);
            engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
            InteropService.Invoke(engine, InteropService.Crypto.ECDsaCheckMultiSig).Should().BeFalse();

            pubkeys = new VMArray
            {
                pubkey1.EncodePoint(false),
                pubkey2.EncodePoint(false)
            };
            signatures = new VMArray();
            engine.CurrentContext.EvaluationStack.Push(signatures);
            engine.CurrentContext.EvaluationStack.Push(pubkeys);
            engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
            InteropService.Invoke(engine, InteropService.Crypto.ECDsaCheckMultiSig).Should().BeFalse();

            pubkeys = new VMArray
            {
                pubkey1.EncodePoint(false),
                pubkey2.EncodePoint(false)
            };
            signatures = new VMArray
            {
                signature1,
                new byte[64]
            };
            engine.CurrentContext.EvaluationStack.Push(signatures);
            engine.CurrentContext.EvaluationStack.Push(pubkeys);
            engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
            InteropService.Invoke(engine, InteropService.Crypto.ECDsaCheckMultiSig).Should().BeTrue();
            engine.CurrentContext.EvaluationStack.Pop().ToBoolean().Should().BeFalse();

            pubkeys = new VMArray
            {
                pubkey1.EncodePoint(false),
                new byte[70]
            };
            signatures = new VMArray
            {
                signature1,
                signature2
            };
            engine.CurrentContext.EvaluationStack.Push(signatures);
            engine.CurrentContext.EvaluationStack.Push(pubkeys);
            engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
            InteropService.Invoke(engine, InteropService.Crypto.ECDsaCheckMultiSig).Should().BeTrue();
            engine.CurrentContext.EvaluationStack.Pop().ToBoolean().Should().BeFalse();
        }

        [TestMethod]
        public void TestAccount_IsStandard()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            var engine = GetEngine(blockchain, false, true);
            var hash = new byte[] { 0x01, 0x01, 0x01 ,0x01, 0x01,
                                    0x01, 0x01, 0x01, 0x01, 0x01,
                                    0x01, 0x01, 0x01, 0x01, 0x01,
                                    0x01, 0x01, 0x01, 0x01, 0x01 };
            engine.CurrentContext.EvaluationStack.Push(hash);
            InteropService.Invoke(engine, InteropService.Contract.IsStandard).Should().BeTrue();
            engine.CurrentContext.EvaluationStack.Pop().ToBoolean().Should().BeTrue();

            var snapshot = blockchain.GetSnapshot();
            var state = TestUtils.GetContract();
            snapshot.Contracts.Add(state.ScriptHash, state);
            engine = new ApplicationEngine(blockchain, TriggerType.Application, null, snapshot, 0);
            engine.LoadScript(new byte[] { 0x01 });
            engine.CurrentContext.EvaluationStack.Push(state.ScriptHash.ToArray());
            InteropService.Invoke(engine, InteropService.Contract.IsStandard).Should().BeTrue();
            engine.CurrentContext.EvaluationStack.Pop().ToBoolean().Should().BeFalse();
        }

        [TestMethod]
        public void TestContract_Create()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            var engine = GetEngine(blockchain, false, true);
            var script = new byte[1024 * 1024 + 1];
            engine.CurrentContext.EvaluationStack.Push(script);
            InteropService.Invoke(engine, InteropService.Contract.Create).Should().BeFalse();

            string manifestStr = new string(new char[ContractManifest.MaxLength + 1]);
            script = new byte[] { 0x01 };
            engine.CurrentContext.EvaluationStack.Push(manifestStr);
            engine.CurrentContext.EvaluationStack.Push(script);
            InteropService.Invoke(engine, InteropService.Contract.Create).Should().BeFalse();

            var manifest = ContractManifest.CreateDefault(UInt160.Parse("0xa400ff00ff00ff00ff00ff00ff00ff00ff00ff01"));
            engine.CurrentContext.EvaluationStack.Push(manifest.ToString());
            engine.CurrentContext.EvaluationStack.Push(script);
            InteropService.Invoke(engine, InteropService.Contract.Create).Should().BeFalse();

            manifest.Abi.Hash = script.ToScriptHash();
            engine.CurrentContext.EvaluationStack.Push(manifest.ToString());
            engine.CurrentContext.EvaluationStack.Push(script);
            InteropService.Invoke(engine, InteropService.Contract.Create).Should().BeTrue();

            var snapshot = blockchain.GetSnapshot();
            var state = TestUtils.GetContract();
            snapshot.Contracts.Add(state.ScriptHash, state);
            engine = new ApplicationEngine(blockchain, TriggerType.Application, null, snapshot, 0);
            engine.LoadScript(new byte[] { 0x01 });
            engine.CurrentContext.EvaluationStack.Push(manifest.ToString());
            engine.CurrentContext.EvaluationStack.Push(state.Script);
            InteropService.Invoke(engine, InteropService.Contract.Create).Should().BeFalse();
        }

        [TestMethod]
        public void TestContract_Update()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            var engine = GetEngine(blockchain, false, true);
            var script = new byte[1024 * 1024 + 1];
            engine.CurrentContext.EvaluationStack.Push(script);
            InteropService.Invoke(engine, InteropService.Contract.Update).Should().BeFalse();

            string manifestStr = new string(new char[ContractManifest.MaxLength + 1]);
            script = new byte[] { 0x01 };
            engine.CurrentContext.EvaluationStack.Push(manifestStr);
            engine.CurrentContext.EvaluationStack.Push(script);
            InteropService.Invoke(engine, InteropService.Contract.Update).Should().BeFalse();

            manifestStr = "";
            engine.CurrentContext.EvaluationStack.Push(manifestStr);
            engine.CurrentContext.EvaluationStack.Push(script);
            InteropService.Invoke(engine, InteropService.Contract.Update).Should().BeFalse();

            var manifest = ContractManifest.CreateDefault(script.ToScriptHash());
            byte[] privkey = { 0x01,0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01};
            KeyPair key = new KeyPair(privkey);
            ECPoint pubkey = key.PublicKey;
            byte[] signature = Crypto.Sign(script.ToScriptHash().ToArray(), privkey, pubkey.EncodePoint(false).Skip(1).ToArray());
            manifest.Groups = new ContractGroup[]
            {
                new ContractGroup()
                {
                    PubKey = pubkey,
                    Signature = signature
                }
            };
            var snapshot = blockchain.GetSnapshot();
            var state = TestUtils.GetContract();
            state.Manifest.Features = ContractFeatures.HasStorage;
            var storageItem = new StorageItem
            {
                Value = new byte[] { 0x01 },
                IsConstant = false
            };

            var storageKey = new StorageKey
            {
                Id = state.Id,
                Key = new byte[] { 0x01 }
            };
            snapshot.Contracts.Add(state.ScriptHash, state);
            snapshot.Storages.Add(storageKey, storageItem);
            engine = new ApplicationEngine(blockchain, TriggerType.Application, null, snapshot, 0);
            engine.LoadScript(state.Script);
            engine.CurrentContext.EvaluationStack.Push(manifest.ToString());
            engine.CurrentContext.EvaluationStack.Push(script);
            InteropService.Invoke(engine, InteropService.Contract.Update).Should().BeTrue();
        }

        [TestMethod]
        public void TestStorage_Find()
        {
            var blockchain = testBlockchain.Container.Blockchain;
            var snapshot = blockchain.GetSnapshot();
            var state = TestUtils.GetContract();
            state.Manifest.Features = ContractFeatures.HasStorage;

            var storageItem = new StorageItem
            {
                Value = new byte[] { 0x01, 0x02, 0x03, 0x04 },
                IsConstant = true
            };
            var storageKey = new StorageKey
            {
                Id = state.Id,
                Key = new byte[] { 0x01 }
            };
            snapshot.Contracts.Add(state.ScriptHash, state);
            snapshot.Storages.Add(storageKey, storageItem);
            var engine = new ApplicationEngine(blockchain, TriggerType.Application, null, snapshot, 0);
            engine.LoadScript(new byte[] { 0x01 });

            engine.CurrentContext.EvaluationStack.Push(new byte[] { 0x01 });
            engine.CurrentContext.EvaluationStack.Push(new InteropInterface(new StorageContext
            {
                Id = state.Id,
                IsReadOnly = false
            }));
            InteropService.Invoke(engine, InteropService.Storage.Find).Should().BeTrue();
            var iterator = ((InteropInterface)engine.CurrentContext.EvaluationStack.Pop()).GetInterface<StorageIterator>();
            iterator.Next();
            var ele = iterator.Value();
            ele.GetSpan().ToHexString().Should().Be(storageItem.Value.ToHexString());

            engine.CurrentContext.EvaluationStack.Push(1);
            InteropService.Invoke(engine, InteropService.Storage.Find).Should().BeFalse();
        }

        [TestMethod]
        public void TestEnumerator_Create()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain);
            var arr = new VMArray {
                new byte[]{ 0x01 },
                new byte[]{ 0x02 }
            };
            engine.CurrentContext.EvaluationStack.Push(arr);
            InteropService.Invoke(engine, InteropService.Enumerator.Create).Should().BeTrue();
            var ret = (InteropInterface)engine.CurrentContext.EvaluationStack.Pop();
            ret.GetInterface<IEnumerator>().Next();
            ret.GetInterface<IEnumerator>().Value().GetSpan().ToHexString()
                .Should().Be(new byte[] { 0x01 }.ToHexString());

            engine.CurrentContext.EvaluationStack.Push(1);
            InteropService.Invoke(engine, InteropService.Enumerator.Create).Should().BeTrue();
        }

        [TestMethod]
        public void TestEnumerator_Next()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain);
            var arr = new VMArray {
                new byte[]{ 0x01 },
                new byte[]{ 0x02 }
            };
            engine.CurrentContext.EvaluationStack.Push(new InteropInterface(new ArrayWrapper(arr)));
            InteropService.Invoke(engine, InteropService.Enumerator.Next).Should().BeTrue();
            engine.CurrentContext.EvaluationStack.Pop().ToBoolean().Should().BeTrue();

            engine.CurrentContext.EvaluationStack.Push(1);
            InteropService.Invoke(engine, InteropService.Enumerator.Next).Should().BeFalse();
        }

        [TestMethod]
        public void TestEnumerator_Value()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain);
            var arr = new VMArray {
                new byte[]{ 0x01 },
                new byte[]{ 0x02 }
            };
            var wrapper = new ArrayWrapper(arr);
            wrapper.Next();
            engine.CurrentContext.EvaluationStack.Push(new InteropInterface(wrapper));
            InteropService.Invoke(engine, InteropService.Enumerator.Value).Should().BeTrue();
            engine.CurrentContext.EvaluationStack.Pop().GetSpan().ToHexString().Should().Be(new byte[] { 0x01 }.ToHexString());

            engine.CurrentContext.EvaluationStack.Push(1);
            InteropService.Invoke(engine, InteropService.Enumerator.Value).Should().BeFalse();
        }

        [TestMethod]
        public void TestEnumerator_Concat()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain);
            var arr1 = new VMArray {
                new byte[]{ 0x01 },
                new byte[]{ 0x02 }
            };
            var arr2 = new VMArray {
                new byte[]{ 0x03 },
                new byte[]{ 0x04 }
            };
            var wrapper1 = new ArrayWrapper(arr1);
            var wrapper2 = new ArrayWrapper(arr2);
            engine.CurrentContext.EvaluationStack.Push(new InteropInterface(wrapper2));
            engine.CurrentContext.EvaluationStack.Push(new InteropInterface(wrapper1));
            InteropService.Invoke(engine, InteropService.Enumerator.Concat).Should().BeTrue();
            var ret = ((InteropInterface)engine.CurrentContext.EvaluationStack.Pop()).GetInterface<IEnumerator>();
            ret.Next().Should().BeTrue();
            ret.Value().GetSpan().ToHexString().Should().Be(new byte[] { 0x01 }.ToHexString());
        }

        [TestMethod]
        public void TestIterator_Create()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain);
            var arr = new VMArray {
                new byte[]{ 0x01 },
                new byte[]{ 0x02 }
            };
            engine.CurrentContext.EvaluationStack.Push(arr);
            InteropService.Invoke(engine, InteropService.Iterator.Create).Should().BeTrue();
            var ret = (InteropInterface)engine.CurrentContext.EvaluationStack.Pop();
            ret.GetInterface<IIterator>().Next();
            ret.GetInterface<IIterator>().Value().GetSpan().ToHexString()
                .Should().Be(new byte[] { 0x01 }.ToHexString());

            var interop = new InteropInterface(1);
            engine.CurrentContext.EvaluationStack.Push(interop);
            InteropService.Invoke(engine, InteropService.Iterator.Create).Should().BeFalse();

            var map = new Map
            {
                [1] = 2,
                [3] = 4
            };
            engine.CurrentContext.EvaluationStack.Push(map);
            InteropService.Invoke(engine, InteropService.Iterator.Create).Should().BeTrue();
            ret = (InteropInterface)engine.CurrentContext.EvaluationStack.Pop();
            ret.GetInterface<IIterator>().Next();
            ret.GetInterface<IIterator>().Key().GetBigInteger().Should().Be(1);
            ret.GetInterface<IIterator>().Value().GetBigInteger().Should().Be(2);

            engine.CurrentContext.EvaluationStack.Push(1);
            InteropService.Invoke(engine, InteropService.Iterator.Create).Should().BeTrue();
        }

        [TestMethod]
        public void TestIterator_Key()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain);
            var arr = new VMArray {
                new byte[]{ 0x01 },
                new byte[]{ 0x02 }
            };
            var wrapper = new ArrayWrapper(arr);
            wrapper.Next();
            engine.CurrentContext.EvaluationStack.Push(new InteropInterface(wrapper));
            InteropService.Invoke(engine, InteropService.Iterator.Key).Should().BeTrue();
            engine.CurrentContext.EvaluationStack.Pop().GetBigInteger().Should().Be(0);

            engine.CurrentContext.EvaluationStack.Push(1);
            InteropService.Invoke(engine, InteropService.Iterator.Key).Should().BeFalse();
        }

        [TestMethod]
        public void TestIterator_Keys()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain);
            var arr = new VMArray {
                new byte[]{ 0x01 },
                new byte[]{ 0x02 }
            };
            var wrapper = new ArrayWrapper(arr);
            engine.CurrentContext.EvaluationStack.Push(new InteropInterface(wrapper));
            InteropService.Invoke(engine, InteropService.Iterator.Keys).Should().BeTrue();
            var ret = ((InteropInterface)engine.CurrentContext.EvaluationStack.Pop()).GetInterface<IteratorKeysWrapper>();
            ret.Next();
            ret.Value().GetBigInteger().Should().Be(0);

            engine.CurrentContext.EvaluationStack.Push(1);
            InteropService.Invoke(engine, InteropService.Iterator.Keys).Should().BeFalse();
        }

        [TestMethod]
        public void TestIterator_Values()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain);
            var arr = new VMArray {
                new byte[]{ 0x01 },
                new byte[]{ 0x02 }
            };
            var wrapper = new ArrayWrapper(arr);
            engine.CurrentContext.EvaluationStack.Push(new InteropInterface(wrapper));
            InteropService.Invoke(engine, InteropService.Iterator.Values).Should().BeTrue();
            var ret = ((InteropInterface)engine.CurrentContext.EvaluationStack.Pop()).GetInterface<IteratorValuesWrapper>();
            ret.Next();
            ret.Value().GetSpan().ToHexString().Should().Be(new byte[] { 0x01 }.ToHexString());

            engine.CurrentContext.EvaluationStack.Push(1);
            InteropService.Invoke(engine, InteropService.Iterator.Values).Should().BeFalse();
        }

        [TestMethod]
        public void TestIterator_Concat()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain);
            var arr1 = new VMArray {
                new byte[]{ 0x01 },
                new byte[]{ 0x02 }
            };
            var arr2 = new VMArray {
                new byte[]{ 0x03 },
                new byte[]{ 0x04 }
            };
            var wrapper1 = new ArrayWrapper(arr1);
            var wrapper2 = new ArrayWrapper(arr2);
            engine.CurrentContext.EvaluationStack.Push(new InteropInterface(wrapper2));
            engine.CurrentContext.EvaluationStack.Push(new InteropInterface(wrapper1));
            InteropService.Invoke(engine, InteropService.Iterator.Concat).Should().BeTrue();
            var ret = ((InteropInterface)engine.CurrentContext.EvaluationStack.Pop()).GetInterface<IIterator>();
            ret.Next().Should().BeTrue();
            ret.Value().GetSpan().ToHexString().Should().Be(new byte[] { 0x01 }.ToHexString());
        }

        [TestMethod]
        public void TestJson_Deserialize()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain);
            engine.CurrentContext.EvaluationStack.Push("1");
            InteropService.Invoke(engine, InteropService.Json.Deserialize).Should().BeTrue();
            var ret = engine.CurrentContext.EvaluationStack.Pop();
            ret.GetBigInteger().Should().Be(1);
        }

        [TestMethod]
        public void TestJson_Serialize()
        {
            var engine = GetEngine(testBlockchain.Container.Blockchain);
            engine.CurrentContext.EvaluationStack.Push(1);
            InteropService.Invoke(engine, InteropService.Json.Serialize).Should().BeTrue();
            var ret = engine.CurrentContext.EvaluationStack.Pop();
            ret.GetString().Should().Be("1");
        }
    }
}
