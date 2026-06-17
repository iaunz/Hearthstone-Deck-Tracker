namespace BgsDataBridge.Projector
{
    public interface IGameSource
    {
        GameStateView Capture();
    }
}
