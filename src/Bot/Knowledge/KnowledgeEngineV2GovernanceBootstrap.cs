using System;

namespace Bot.Knowledge
{
    internal static class KnowledgeEngineV2GovernanceBootstrap
    {
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (System.Threading.Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                try { KnowledgeV2RevisionUiBridge.Initialize(); } catch { }
                try { KnowledgeV2GovernanceUiBridge.Initialize(); } catch { }
                try { KnowledgeV2OperatorUiBridge.Initialize(); } catch { }
            }
            return new object();
        }
    }
}

namespace Bot
{
    public partial class App
    {
        private readonly object _knowledgeV2GovernanceBootstrap =
            Knowledge.KnowledgeEngineV2GovernanceBootstrap.InitializeForApp();
    }
}
