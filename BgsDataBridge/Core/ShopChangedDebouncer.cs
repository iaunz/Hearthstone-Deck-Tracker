namespace BgsDataBridge.Core
{
    public delegate void ShopEmitted(string payload);

    // trailing-edge: 变化后静默 quietMs 再发；同窗口多次变化只发最终态。
    public class ShopChangedDebouncer
    {
        private readonly long _quietMs;
        private readonly IClock _clock;
        private string _pending;
        private long _lastChangeAt = -1;
        private bool _dirty;

        public event ShopEmitted OnEmit;

        public ShopChangedDebouncer(long quietMs, IClock clock)
        {
            _quietMs = quietMs;
            _clock = clock;
        }

        public void Update(string payload, long nowMs)
        {
            _pending = payload;
            _lastChangeAt = nowMs;
            _dirty = true;
        }

        public void Tick()
        {
            if (!_dirty || _lastChangeAt < 0) return;
            if (_clock.NowMs - _lastChangeAt >= _quietMs) Flush();
        }

        public void Flush()
        {
            if (!_dirty) return;
            var p = _pending;
            _dirty = false;
            _lastChangeAt = -1;
            OnEmit?.Invoke(p);
        }
    }
}
