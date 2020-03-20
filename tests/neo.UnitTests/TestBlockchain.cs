using System;
using Neo.Persistence;

namespace Neo.UnitTests
{
    public class TestBlockchain
    {
        private readonly NeoSystem _neoSystem;
        private readonly NeoContainer _container;

        public TestBlockchain()
        {
            _container = new NeoContainer();
            var memoryPool = _container.ResolveMemoryPool();
            var blockchainActor = _container.ResolveBlockchainActor(memoryPool, new MemoryStore());
            var _blockchainActorPath = blockchainActor.Path;

            // Ensure that blockchain is loaded
            _neoSystem = _container.ResolveNeoSystem(_container.Blockchain.Store);
            var _snapshot = _container.Blockchain.GetSnapshot();
        }

        public void InitializeMockNeoSystem()
        {
        }

        public NeoSystem NeoSystem => _neoSystem;
        public NeoContainer Container => _container;
    }
}
