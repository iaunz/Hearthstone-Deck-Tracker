namespace BgsDataBridge.Core
{
    // trailing-edge: 变更后静默 quietMs 再发；同窗口多次变化只发最终态（pending）。
    // 泛型 T 为 pending 载荷（plugin 用 ShopSnapshot）。Core 不依赖 Projector。
    public class ShopChangedDebouncer<T>
    {
        private readonly long _quietMs;
        private readonly IClock _clock;
        private T _pending;
        private long _lastChangeAt = -1;
        private bool _dirty;

        public event System.Action<T> OnEmit;

        public ShopChangedDebouncer(long quietMs, IClock clock)
        {
            _quietMs = quietMs;
            _clock = clock;
        }

        public void Update(T payload, long nowMs)
        {
            _pending = payload;
            _lastChangeAt = nowMs;
            _dirty = true;
        }

        public void Tick()
        {
            if(!_dirty || _lastChangeAt < 0) return;
            if(_clock.NowMs - _lastChangeAt >= _quietMs) Flush();
        }

        public void Flush()
        {
            if(!_dirty) return;
            var p = _pending;
            _dirty = false;
            _lastChangeAt = -1;
            OnEmit?.Invoke(p);
        }

        // 丢弃未发出的 pending（Shop→Combat 切换时调用，避免战斗开始时的陈旧/空发）。
        public void Reset()
        {
            _pending = default(T);
            _dirty = false;
            _lastChangeAt = -1;
        }
    }
}
