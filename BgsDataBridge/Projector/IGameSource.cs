namespace BgsDataBridge.Projector
{
    public interface IGameSource
    {
        /// <summary>
        /// Full capture: every slice (lobby, races, rating, board, shop,
        /// lastOpponent). Used by the HTTP /state route.
        /// </summary>
        GameStateView Capture();
    }
}
