using Autofac;
using Neo.Consensus;
using Neo.Ledger;
using Neo.Network.P2P;

namespace Neo
{
    public class NeoContainer
    {
        public readonly ContainerBuilder Builder;
        private IContainer _container;

        public NeoContainer()
        {
            Builder = new ContainerBuilder();
            Builder.RegisterType<Blockchain>().AsSelf();
            Builder.RegisterType<LocalNode>().AsSelf();
            Builder.RegisterType<TaskManager>().AsSelf();
            Builder.RegisterType<ConsensusService>().AsSelf();
        }

        public IContainer Container => _container ?? (_container = Builder.Build());
    }
}