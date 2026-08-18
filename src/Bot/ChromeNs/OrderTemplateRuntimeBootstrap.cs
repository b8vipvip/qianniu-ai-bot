namespace Bot
{
    /// <summary>
    /// Guarantees the order-template runtime/UI bootstrap executes for every Bot process.
    /// A never-read static field on a partial App type is not sufficient because the CLR may mark
    /// the type beforefieldinit; an explicit static constructor removes that ambiguity.
    /// </summary>
    public partial class App
    {
        static App()
        {
            ChromeNs.OrderTemplateRequiredFieldsV2.InitializeForApp();
        }
    }
}
