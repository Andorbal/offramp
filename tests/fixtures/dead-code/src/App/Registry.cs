using System.Reflection;

namespace DeadCode.App
{
    /// <summary>Stands in for a container's assembly scanning (Scrutor's Scan, Autofac's RegisterAssemblyTypes).</summary>
    internal sealed class Registry
    {
        public void Scan(Assembly assembly)
        {
            System.Console.WriteLine(assembly.GetTypes().Length);
        }
    }
}
