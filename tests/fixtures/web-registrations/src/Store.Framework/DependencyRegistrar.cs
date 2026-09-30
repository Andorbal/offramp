using Autofac;
using Autofac.Integration.Mvc;
using Store.Framework.Controllers;
using Store.Framework.Filters;

namespace Store.Framework
{
    /// <summary>Filters registered with the container, for controller base classes (SmartStoreNET's pattern).</summary>
    public static class DependencyRegistrar
    {
        public static void Register(ContainerBuilder builder)
        {
            builder.RegisterType<HandleExceptionFilter>()
                .AsExceptionFilterFor<StoreController>(-100)
                .AsActionFilterFor<StoreController>(int.MaxValue)
                .InstancePerRequest();

            builder.RegisterType<CookieConsentFilter>()
                .AsActionFilterFor<PublicControllerBase>()
                .InstancePerRequest();

            builder.RegisterType<MenuResultFilter>().AsResultFilterFor<StoreController>(0);
            builder.RegisterFilterProvider();
        }
    }
}
