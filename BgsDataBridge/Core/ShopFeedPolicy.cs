namespace BgsDataBridge.Core
{
    // 纯函数：决定是否把一次商店采样喂给去抖器。空商店（已买空）无决策价值
    // 且会产生空载荷噪声；非购物相（战斗/菜单）不喂。
    public static class ShopFeedPolicy
    {
        public static bool ShouldFeed(int offerCount, bool inShopPhase)
            => inShopPhase && offerCount > 0;
    }
}
