using System;
using Neo.Ledger;
using Neo.Persistence;

namespace Neo.UnitTests
{
    public class TestBlockchain
    {
        private readonly NeoSystem _neoSystem;
        private readonly NeoContainer _container;
        private readonly MemoryPool _memoryPool;

        public TestBlockchain()
        {
            _container = new NeoContainer();
            _memoryPool = _container.ResolveMemoryPool();
            var blockchainActor = _container.ResolveBlockchainActor(_memoryPool, new MemoryStore());
            var _blockchainActorPath = blockchainActor.Path;

            // Ensure that blockchain is loaded
            _neoSystem = _container.ResolveNeoSystem(_container.Blockchain.Store);
        }

        public void InitializeMockNeoSystem()
        {
        }

        public NeoSystem NeoSystem => _neoSystem;
        public NeoContainer Container => _container;
        public MemoryPool MemoryPool=> _memoryPool;
    }
}
