using System.Web.Optimization;

namespace Store.Web.Infrastructure
{
    public static class BundleConfig
    {
        public static void RegisterBundles(BundleCollection bundles)
        {
            bundles.Add(new ScriptBundle("~/bundles/app").Include("~/Scripts/app.js"));

            var scriptBundle = new ScriptBundle("~/bundles/vendor").Include(
                "~/Scripts/vendor/jquery.js",
                "~/Scripts/vendor/bootstrap.js");
            bundles.Add(scriptBundle);
        }
    }
}
