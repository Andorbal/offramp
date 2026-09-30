namespace Evidence.Engine
{
    public interface IStartupTask
    {
        void Execute();
    }

    /// <summary>Named by nothing: Bootstrapper finds it with FindClassesOfType&lt;IStartupTask&gt;().</summary>
    public sealed class CacheWarmupTask : IStartupTask
    {
        public void Execute()
        {
            System.Console.WriteLine("warm");
        }
    }

    public interface IMapper<TFrom, TTo>
    {
        TTo Map(TFrom from);
    }

    /// <summary>Named by nothing: Bootstrapper finds it with FindClassesOfType(typeof(IMapper&lt;,&gt;)).</summary>
    public sealed class OrderMapper : IMapper<int, string>
    {
        public string Map(int from)
        {
            return "order " + from;
        }
    }

    /// <summary>Implements an interface nothing looks for: dead.</summary>
    public sealed class UnusedTask : ICloneableTask
    {
        public object CloneTask()
        {
            return this;
        }
    }

    public interface ICloneableTask
    {
        object CloneTask();
    }
}
