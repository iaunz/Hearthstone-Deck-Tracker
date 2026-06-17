using BgsDataBridge.Events;
namespace BgsDataBridge.Core
{
    public struct TriggerEvent
    {
        public BridgeEventType Type;
        public TriggerEvent(BridgeEventType t) { Type = t; }
    }
}
