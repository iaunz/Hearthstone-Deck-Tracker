namespace BgsDataBridge.Core
{
    // 酒馆等级上升沿检测。战棋 tier 单调递增（不可能降）。
    // 首帧建基线不触发；仅严格上升返回 (from,to)。纯逻辑、可单测。
    public class TierEdgeTracker
    {
        int _last = -1;   // -1 = 未建基线

        public (int from, int to)? Observe(int tier)
        {
            if (_last < 0)
            {
                _last = tier;
                return null;
            }
            if (tier > _last)
            {
                var r = (_last, tier);
                _last = tier;
                return r;
            }
            _last = tier;
            return null;
        }

        public void Reset() => _last = -1;
    }
}
