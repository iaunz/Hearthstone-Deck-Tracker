namespace BgsDataBridge.Webhook
{
    public interface IHttpSender { int Send(string url, string body, string signature, int timeoutMs); }
}
