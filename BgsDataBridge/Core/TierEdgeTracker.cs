namespace BgsDataBridge.Core
{
    // 酒馆等级上升沿检测。战棋 tier 单调递增（不可能降）。
    // 首帧建基线不触发；仅严格上升返回 (from,to)。纯逻辑、可单测。
    // tier<=0 视为"未知"（开局 ReadTechLevel 在英雄 PLAYER_TECH_LEVEL 建立前返回 0），
    // 忽略、不动基线——避免 spurious {0→1}。真实 tier 为 1..6。
    public class TierEdgeTracker
    {
        int _last = -1;   // -1 = 未建基线

        public (int from, int to)? Observe(int tier)
        {
            if (tier <= 0) return null;   // 0/负 = 未建立，忽略，不动基线
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
