using System;
using System.Linq;

namespace Evidence.Engine
{
    public static class Bootstrapper
    {
        public static void Run(ITypeFinder finder)
        {
            foreach (var type in finder.FindClassesOfType<IStartupTask>())
            {
                ((IStartupTask)Activator.CreateInstance(type)).Execute();
            }

            Console.WriteLine(finder.FindClassesOfType(typeof(IMapper<,>)).Count());
            Console.WriteLine(ModelBuilder.CreateMaps().Count);
            Console.WriteLine(ModelBuilder.IsList(typeof(int)));
        }
    }
}
