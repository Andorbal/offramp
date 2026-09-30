using System;
using System.Collections.Generic;
using System.Linq;

namespace Evidence.Engine
{
    /// <summary>Stands in for EF6's EntityTypeConfiguration&lt;T&gt;.</summary>
    public abstract class EntityMap<T>
    {
    }

    public sealed class Product
    {
    }

    /// <summary>Named by nothing: ModelBuilder finds it by its base type's generic definition, as SmartObjectContext finds its maps.</summary>
    public sealed class ProductMap : EntityMap<Product>
    {
    }

    /// <summary>Derives from a generic type the solution only inspects: dead.</summary>
    public sealed class OrderList : List<int>
    {
    }

    public static class ModelBuilder
    {
        public static List<object> CreateMaps()
        {
            var maps = from t in typeof(ModelBuilder).Assembly.GetTypes()
                       where t.BaseType != null && t.BaseType.IsGenericType
                       let definition = t.BaseType.GetGenericTypeDefinition()
                       where definition == typeof(EntityMap<>)
                       select Activator.CreateInstance(t);
            return maps.ToList();
        }

        public static bool IsList(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
        }
    }
}
