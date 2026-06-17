using System;
using System.Security.Cryptography;
using System.Text;

namespace BgsDataBridge.Webhook
{
    public static class HmacSigner
    {
        public static string Sign(string body, string secret)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? "")))
                return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(body ?? "")))
                    .Replace("-", "").ToLowerInvariant();
        }
    }
}
