namespace BgsDataBridge.Core
{
    // 纯函数：判断当前是否处于"英雄选择"相位。用游戏实体 STEP tag 做硬门
    // （单调、常驻），避免旧的 IsBattlegroundsHeroPickingDone（依赖玩家实体
    // MULLIGAN_STATE）在玩家实体瞬时缺失时误读 false 而触发赛中 HeroPick。
    // step 缺失时调用方应传 int.MaxValue（永不触发）。
    public static class HeroPickPhase
    {
        public static bool IsActive(bool isBattlegrounds, bool isInMenu, int step, int beginMulliganStep)
            => isBattlegrounds && !isInMenu && step <= beginMulliganStep;
    }
}
