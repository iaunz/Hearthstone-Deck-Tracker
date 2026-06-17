namespace BgsDataBridge.Projector
{
    public interface IGameSource
    {
        /// <summary>
        /// Full capture: every slice (lobby, races, rating, board, shop,
        /// lastOpponent). Used by the HTTP /state route.
        /// </summary>
        GameStateView Capture();

        /// <summary>
        /// Shop-only capture (I3): reads only <c>GetOpponentBoardState()</c>
        /// + turn/phase. Skips lobby/races/rating/board/lastOpponent — the
        /// shop-poll path runs at 10Hz during shopping, so a full Capture()
        /// there would be far heavier than spec §5.2 requires. The returned
        /// view has ONLY <see cref="GameStateView.Shop"/> + Turn + Phase +
        /// InMatch/IsBattlegrounds populated (for projection). Returns null
        /// when not in a BGs shopping phase.
        /// </summary>
        GameStateView CaptureShopOnly();
    }
}
