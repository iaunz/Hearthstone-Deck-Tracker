using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgsDataBridge.Tests
{
    [TestClass]
    public class SanityTest
    {
        [TestMethod]
        public void Sanity_Passes()
        {
            Assert.AreEqual(2, 1 + 1);
        }
    }
}
