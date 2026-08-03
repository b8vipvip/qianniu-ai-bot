namespace Bot.ChromeNs
{
    // Keeps the startup call beside the handoff service while the actual list
    // management implementation lives with the knowledge UI types.
    internal static class BulkListManagementUi
    {
        public static void Initialize()
        {
            Bot.Knowledge.BulkListManagementUi.Initialize();
        }
    }
}
