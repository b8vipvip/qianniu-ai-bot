namespace Bot.ChromeNs
{
    // Keeps startup calls beside the handoff service while the actual list
    // management implementation lives with the knowledge UI types.
    internal static class BulkListManagementUi
    {
        public static void Initialize()
        {
            // The local policy has already been initialized at this point. Run
            // the authenticated legacy import in the background and replace it
            // through SaveRules only when the old server snapshot is available.
            HandoffPolicyLegacyMigrationService.StartOnce();
            Bot.Knowledge.BulkListManagementUi.Initialize();
        }
    }
}
