namespace BgsDataBridge.Projector
{
    // 商店去抖器的 pending 载荷：随 Update 一起捕获的（shop 快照 + 当时 turn/phase）。
    // emit 时直接用它构造 webhook，不再"重新捕获"（offers 丢失的真正修复点）。
    public class ShopSnapshot
    {
        public ShopView Shop;
        public int Turn;
        public string Phase;
    }
}
