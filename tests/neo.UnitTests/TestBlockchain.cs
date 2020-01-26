using Neo.Ledger;
using System;
using Neo.Persistence;

namespace Neo.UnitTests
{
    public static class TestBlockchain
    {
        public static readonly NeoSystem TheNeoSystem;
        public static readonly NeoContainer Container;

        static TestBlockchain()
        {
            Container = new NeoContainer();
            var memoryPool = Container.ResolveMemoryPool();
            Container.ResolveBlockchainActor(memoryPool, new MemoryStore());

            Console.WriteLine("initialize NeoSystem");
            TheNeoSystem = Container.ResolveNeoSystem(Container.Blockchain.Store);

            // Ensure that blockchain is loaded
            // TODO @rodoufu check this
            var _ = Container.Blockchain.Height;
        }

        public static void InitializeMockNeoSystem()
        {
        }
    }
}
