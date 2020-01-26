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
            var blockchainActor = Container.ResolveBlockchainActor(memoryPool, new MemoryStore());
            var _ = blockchainActor.Path;

            Console.WriteLine("initialize NeoSystem");
            // Ensure that blockchain is loaded
            TheNeoSystem = Container.ResolveNeoSystem(Container.Blockchain.Store);
        }

        public static void InitializeMockNeoSystem()
        {
        }
    }
}
