namespace Shared;

public static class Clock
{
#if NETFRAMEWORK
    public static System.DateTime UtcNow() => System.DateTime.UtcNow;
#else
    public static System.DateTime UtcNow() => System.TimeProvider.System.GetUtcNow().UtcDateTime;
#endif
}
