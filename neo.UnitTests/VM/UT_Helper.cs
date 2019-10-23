using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Cryptography.ECC;
using Neo.SmartContract;
using Neo.VM;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Neo.UnitTests.IO
{
    [TestClass]
    public class UT_Helper
    {
        [TestMethod]
        public void TestEmit()
        {
            ScriptBuilder sb = new ScriptBuilder();
            sb.Emit(new OpCode[] { OpCode.PUSH0 });
            Assert.AreEqual(Encoding.Default.GetString(new byte[] { 0x00 }), Encoding.Default.GetString(sb.ToArray()));
        }

        [TestMethod]
        public void TestEmitAppCall1()
        {
            //format:(byte)0x00+(byte)OpCode.NEWARRAY+(string)operation+(Uint160)scriptHash+(uint)InteropService.System_Contract_Call
            ScriptBuilder sb = new ScriptBuilder();
            sb.EmitAppCall(UInt160.Zero, "AAAAA");
            byte[] tempArray = new byte[34];
            tempArray[0] = 0x00;//0
            tempArray[1] = 0xC5;//OpCode.NEWARRAY 
            tempArray[2] = 5;//operation.Length
            Array.Copy(Encoding.UTF8.GetBytes("AAAAA"), 0, tempArray, 3, 5);//operation.data
            tempArray[8] = 0x14;//scriptHash.Length
            Array.Copy(UInt160.Zero.ToArray(), 0, tempArray, 9, 20);//operation.data
            uint api = InteropService.System_Contract_Call;
            tempArray[29] = 0x68;//OpCode.SYSCALL
            Array.Copy(BitConverter.GetBytes(api), 0, tempArray, 30, 4);//api.data
            byte[] resultArray = sb.ToArray();
            Assert.AreEqual(Encoding.Default.GetString(tempArray), Encoding.Default.GetString(resultArray));
        }

        [TestMethod]
        public void TestEmitAppCall2()
        {
            //format:(ContractParameter[])ContractParameter+(byte)OpCode.PACK+(string)operation+(Uint160)scriptHash+(uint)InteropService.System_Contract_Call
            ScriptBuilder sb = new ScriptBuilder();
            sb.EmitAppCall(UInt160.Zero, "AAAAA", new ContractParameter[] { new ContractParameter(ContractParameterType.Integer) });
            byte[] tempArray = new byte[35];
            tempArray[0] = 0x00;//0
            tempArray[1] = 0x51;//ContractParameter.Length 
            tempArray[2] = 0xC1;//OpCode.PACK
            tempArray[3] = 0x05;//operation.Length
            Array.Copy(Encoding.UTF8.GetBytes("AAAAA"), 0, tempArray, 4, 5);//operation.data
            tempArray[9] = 0x14;//scriptHash.Length
            Array.Copy(UInt160.Zero.ToArray(), 0, tempArray, 10, 20);//operation.data
            uint api = InteropService.System_Contract_Call;
            tempArray[30] = 0x68;//OpCode.SYSCALL
            Array.Copy(BitConverter.GetBytes(api), 0, tempArray, 31, 4);//api.data
            byte[] resultArray = sb.ToArray();
            Assert.AreEqual(Encoding.Default.GetString(tempArray), Encoding.Default.GetString(resultArray));
        }

        [TestMethod]
        public void TestEmitAppCall3()
        {
            //format:(object[])args+(byte)OpCode.PACK+(string)operation+(Uint160)scriptHash+(uint)InteropService.System_Contract_Call
            ScriptBuilder sb = new ScriptBuilder();
            sb.EmitAppCall(UInt160.Zero, "AAAAA", true);
            byte[] tempArray = new byte[35];
            tempArray[0] = 0x51;//arg
            tempArray[1] = 0x51;//args.Length 
            tempArray[2] = 0xC1;//OpCode.PACK
            tempArray[3] = 0x05;//operation.Length
            Array.Copy(Encoding.UTF8.GetBytes("AAAAA"), 0, tempArray, 4, 5);//operation.data
            tempArray[9] = 0x14;//scriptHash.Length
            Array.Copy(UInt160.Zero.ToArray(), 0, tempArray, 10, 20);//operation.data
            uint api = InteropService.System_Contract_Call;
            tempArray[30] = 0x68;//OpCode.SYSCALL
            Array.Copy(BitConverter.GetBytes(api), 0, tempArray, 31, 4);//api.data
            byte[] resultArray = sb.ToArray();
            Assert.AreEqual(Encoding.Default.GetString(tempArray), Encoding.Default.GetString(resultArray));
        }

        [TestMethod]
        public void TestToParameter()
        {
            StackItem byteItem = "00e057eb481b".HexToBytes();
            Assert.AreEqual(30000000000000L, (long)new BigInteger(byteItem.ToParameter().Value as byte[]));

            StackItem boolItem = false;
            Assert.AreEqual(false, (bool)boolItem.ToParameter().Value);

            StackItem intItem = new BigInteger(1000);
            Assert.AreEqual(1000, (BigInteger)intItem.ToParameter().Value);

            StackItem interopItem = new VM.Types.InteropInterface<string>("test");
            Assert.AreEqual(null, interopItem.ToParameter().Value);

            StackItem arrayItem = new VM.Types.Array(new[] { byteItem, boolItem, intItem, interopItem });
            Assert.AreEqual(1000, (BigInteger)(arrayItem.ToParameter().Value as List<ContractParameter>)[2].Value);

            StackItem mapItem = new VM.Types.Map(new Dictionary<StackItem, StackItem>(new[] { new KeyValuePair<StackItem, StackItem>(byteItem, intItem) }));
            Assert.AreEqual(1000, (BigInteger)(mapItem.ToParameter().Value as List<KeyValuePair<ContractParameter, ContractParameter>>)[0].Value.Value);
        }

        [TestMethod]
        public void TestToStackItem()
        {
            ContractParameter byteParameter = new ContractParameter { Type = ContractParameterType.ByteArray, Value = "00e057eb481b".HexToBytes() };
            Assert.AreEqual(30000000000000L, (long)byteParameter.ToStackItem().GetBigInteger());

            ContractParameter boolParameter = new ContractParameter { Type = ContractParameterType.Boolean, Value = false };
            Assert.AreEqual(false, boolParameter.ToStackItem().GetBoolean());

            ContractParameter intParameter = new ContractParameter { Type = ContractParameterType.Integer, Value = new BigInteger(1000) };
            Assert.AreEqual(1000, intParameter.ToStackItem().GetBigInteger());

            ContractParameter h160Parameter = new ContractParameter { Type = ContractParameterType.Hash160, Value = UInt160.Zero };
            Assert.AreEqual(0, h160Parameter.ToStackItem().GetBigInteger());

            ContractParameter h256Parameter = new ContractParameter { Type = ContractParameterType.Hash256, Value = UInt256.Zero };
            Assert.AreEqual(0, h256Parameter.ToStackItem().GetBigInteger());

            ContractParameter pkParameter = new ContractParameter { Type = ContractParameterType.PublicKey, Value = ECPoint.Parse("02f9ec1fd0a98796cf75b586772a4ddd41a0af07a1dbdf86a7238f74fb72503575", ECCurve.Secp256r1) };
            Assert.AreEqual("02f9ec1fd0a98796cf75b586772a4ddd41a0af07a1dbdf86a7238f74fb72503575", pkParameter.ToStackItem().GetByteArray().ToHexString());

            ContractParameter strParameter = new ContractParameter { Type = ContractParameterType.String, Value = "test😂👍" };
            Assert.AreEqual("test😂👍", Encoding.UTF8.GetString(strParameter.ToStackItem().GetByteArray()));

            ContractParameter interopParameter = new ContractParameter { Type = ContractParameterType.InteropInterface };
            Assert.AreEqual(null, interopParameter.ToStackItem());

            ContractParameter arrayParameter = new ContractParameter { Type = ContractParameterType.Array, Value = new[] { byteParameter, boolParameter, intParameter, h160Parameter, h256Parameter, pkParameter, strParameter, interopParameter }.ToList() };
            Assert.AreEqual(1000, ((VM.Types.Array)arrayParameter.ToStackItem())[2].GetBigInteger());

            ContractParameter mapParameter = new ContractParameter { Type = ContractParameterType.Map, Value = new[] { new KeyValuePair<ContractParameter, ContractParameter>(byteParameter, pkParameter) } };
            Assert.AreEqual(30000000000000L, (long)((VM.Types.Map)mapParameter.ToStackItem()).Keys.First().GetBigInteger());
        }
    }
}
