using System;
using System.Net;
using System.Threading;
using PeakMusic.Core.Netease;

namespace EapiHarness
{
    /// <summary>
    /// 游戏外实机复现 NeteaseClient.QrBeginAsync 的 eapi 请求。
    /// 用途：定位「eapi resp: HTTP 200 len=0 空响应」到底差在哪。
    /// </summary>
    internal static class Program
    {
        private static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Console.WriteLine("Expect100Continue(before) = " + ServicePointManager.Expect100Continue);
            if (args.Length > 0 && args[0] == "noexpect")
            {
                ServicePointManager.Expect100Continue = false;
                Console.WriteLine("-> set Expect100Continue = false");
            }

            try
            {
                using (var client = new NeteaseClient())
                {
                    string unikey = client.QrBeginAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine("UNIKEY OK: " + unikey);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL: " + e.GetType().Name + ": " + e.Message);
                if (e.InnerException != null)
                    Console.WriteLine("INNER: " + e.InnerException.Message);
            }
        }
    }
}
