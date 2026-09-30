using System;
using System.Collections.Generic;
using System.Linq;

namespace Evidence.Engine
{
    /// <summary>SmartStoreNET's ITypeFinder: the container setup finds its registrars, startup tasks, and mappers with it.</summary>
    public interface ITypeFinder
    {
        IEnumerable<Type> FindClassesOfType(Type assignTypeFrom);
    }

    public static class TypeFinderExtensions
    {
        public static IEnumerable<Type> FindClassesOfType<T>(this ITypeFinder finder)
        {
            return finder.FindClassesOfType(typeof(T));
        }
    }

    public sealed class AppDomainTypeFinder : ITypeFinder
    {
        public IEnumerable<Type> FindClassesOfType(Type assignTypeFrom)
        {
            return typeof(AppDomainTypeFinder).Assembly.GetTypes().Where(t => assignTypeFrom.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);
        }
    }
}
