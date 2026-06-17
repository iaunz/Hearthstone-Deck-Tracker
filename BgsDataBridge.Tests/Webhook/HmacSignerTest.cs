using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Webhook;

namespace BgsDataBridge.Tests.Webhook
{
    [TestClass]
    public class HmacSignerTest
    {
        [TestMethod]
        public void Sign_Matches_Known_Vector()
        {
            // key="k", body="hello" → HMAC-SHA256 lowerhex
            var sig = HmacSigner.Sign("hello", "k");
            // 用任意标准库离线算出的定值；实现须与此一致
            Assert.AreEqual("406e4b43f87095aa86ca6299d25e875921fefa180f02043bb29bec5681c0c2d0", sig);
        }

        [TestMethod]
        public void Sign_Differs_For_Different_Input()
        {
            Assert.AreNotEqual(HmacSigner.Sign("a", "k"), HmacSigner.Sign("b", "k"));
        }
    }
}
