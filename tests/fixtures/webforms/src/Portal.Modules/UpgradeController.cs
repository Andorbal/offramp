namespace Portal.Modules
{
    // Named only by the Portal.Modules.dnn manifest, which the portal reads to create it.
    public class UpgradeController
    {
        public string UpgradeModule(string version) => "upgraded to " + version;
    }
}
