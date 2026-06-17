namespace BgsDataBridge.Core
{
    public interface IClock { long NowMs { get; } }
    public class SystemClock : IClock { public long NowMs => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
}
