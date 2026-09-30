using Autofac;
using Autofac.Integration.Mvc;
using Store.Web.Controllers;

namespace Store.Web.Infrastructure
{
    public static class DependencyRegistrar
    {
        public static void Register(ContainerBuilder builder)
        {
            // A filter for one action of the site's controller.
            builder.RegisterType<PreviewFilter>().AsResultFilterFor<HomeController>(x => x.Index()).InstancePerRequest();
        }
    }
}
