using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PclDl
{
    /// <summary>一次 DNS 优选的结果</summary>
    public class DnsChoice
    {
        public string Server;      // DNS 服务器地址
        public string ServerName;  // 显示名
        public string Ip;          // 该 DNS 解析出的、连接最快的 IP
        public int DnsMs;          // DNS 查询耗时
        public int TcpMs;          // TCP 握手耗时
        public bool FromSystem;    // 是否来自系统 DNS
    }

    /// <summary>
    /// DNS 优选：把常见公共 DNS 全测一遍，选"解析快 + 连得上且握手快"的那个。
    /// 只做 A 记录（IPv4），够用且兼容老网络。
    /// </summary>
    public static class DnsOptimizer
    {
        private class Server
        {
            public string Ip;
            public string Name;
        }

        private static readonly Server[] Servers =
        {
            new Server { Ip = "223.5.5.5",   Name = "阿里 DNS" },
            new Server { Ip = "119.29.29.29",Name = "腾讯 DNS" },
            new Server { Ip = "114.114.114.114", Name = "114DNS" },
            new Server { Ip = "180.76.76.76",Name = "百度 DNS" },
            new Server { Ip = "1.1.1.1",     Name = "Cloudflare" },
            new Server { Ip = "8.8.8.8",     Name = "Google DNS" },
            new Server { Ip = "9.9.9.9",     Name = "Quad9" },
        };

        private static readonly ConcurrentDictionary<string, DnsChoice> Cache =
            new ConcurrentDictionary<string, DnsChoice>();

        private static readonly ConcurrentDictionary<string, long> CacheTime =
            new ConcurrentDictionary<string, long>();

        private static readonly object PickLock = new object();

        /// <summary>缓存 5 分钟，避免每个分块都重测</summary>
        public static DnsChoice Pick(string host, int port, Action<string> log)
        {
            string key = host + ":" + port;
            long now = Environment.TickCount;

            long ts;
            if (CacheTime.TryGetValue(key, out ts) && now - ts < 300000)
            {
                DnsChoice c;
                if (Cache.TryGetValue(key, out c) && c != null) return c;
            }

            lock (PickLock)
            {
                if (CacheTime.TryGetValue(key, out ts) && now - ts < 300000)
                {
                    DnsChoice c2;
                    if (Cache.TryGetValue(key, out c2) && c2 != null) return c2;
                }

                DnsChoice best = null;
                var all = new List<DnsChoice>();
                var gate = new object();
                int pending = Servers.Length + 1;
                var finished = new ManualResetEventSlim(false);

                Action<DnsChoice> add = c =>
                {
                    if (c == null) return;
                    lock (gate)
                    {
                        all.Add(c);
                        if (best == null || Score(c) < Score(best)) best = c;
                    }
                    if (Interlocked.Decrement(ref pending) == 0) finished.Set();
                };

                foreach (var s in Servers)
                {
                    var sv = s;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            var ips = QueryA(sv.Ip, host, 1500);
                            if (ips.Count == 0) return;
                            int dnsMs = LastQueryMs;

                            int bestTcp = int.MaxValue;
                            string bestIp = null;
                            foreach (var ip in ips)
                            {
                                int t = TcpPing(ip, port, 2000);
                                if (t >= 0 && t < bestTcp) { bestTcp = t; bestIp = ip; }
                            }
                            if (bestIp == null) return;

                            add(new DnsChoice
                            {
                                Server = sv.Ip,
                                ServerName = sv.Name,
                                Ip = bestIp,
                                DnsMs = dnsMs,
                                TcpMs = bestTcp
                            });
                        }
                        catch { }
                        finally
                        {
                            if (Interlocked.Decrement(ref pending) == 0) finished.Set();
                        }
                    });
                }

                // 系统 DNS 也参与比较，免得公共 DNS 反而不如运营商
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var addrs = Dns.GetHostAddresses(host);
                        int bestTcp = int.MaxValue;
                        string bestIp = null;
                        foreach (var a in addrs)
                        {
                            if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                            int t = TcpPing(a.ToString(), port, 2000);
                            if (t >= 0 && t < bestTcp) { bestTcp = t; bestIp = a.ToString(); }
                        }
                        if (bestIp != null)
                            add(new DnsChoice
                            {
                                Server = "系统",
                                ServerName = "系统 DNS",
                                Ip = bestIp,
                                DnsMs = 0,
                                TcpMs = bestTcp,
                                FromSystem = true
                            });
                    }
                    catch { }
                    finally
                    {
                        if (Interlocked.Decrement(ref pending) == 0) finished.Set();
                    }
                });

                finished.Wait(4000);

                if (log != null)
                {
                    foreach (var c in all)
                        log(string.Format("DNS 测试 {0,-12} {1,4}ms  → {2}（握手 {3}ms）",
                            c.ServerName, c.DnsMs, c.Ip, c.TcpMs));
                }

                if (best != null)
                {
                    Cache[key] = best;
                    CacheTime[key] = now;
                    if (log != null)
                        log(string.Format("DNS 优选结果：{0}（{1}）→ {2}", best.ServerName, best.Server, best.Ip));
                }

                return best;
            }
        }

        private static double Score(DnsChoice c)
        {
            return c.TcpMs * 0.7 + Math.Min(c.DnsMs, 2000) * 0.3;
        }

        [ThreadStatic] private static int _lastQueryMs;

        private static int LastQueryMs
        {
            get { return _lastQueryMs; }
        }

        /// <summary>发一个 A 查询，返回 A 记录；overhead=true 时顺便记录耗时</summary>
        private static List<string> QueryA(string dnsServer, string host, int timeoutMs)
        {
            var result = new List<string>();
            long t0 = Environment.TickCount;

            byte[] packet = BuildQuery(host, (ushort)(Environment.TickCount & 0xFFFF));
            using (var udp = new UdpClient())
            {
                udp.Client.ReceiveTimeout = timeoutMs;
                try
                {
                    udp.Send(packet, packet.Length, dnsServer, 53);
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    byte[] resp = udp.Receive(ref ep);
                    ParseAnswers(resp, result);
                }
                catch { }
            }

            _lastQueryMs = (int)(Environment.TickCount - t0);
            return result;
        }

        private static byte[] BuildQuery(string host, ushort id)
        {
            var ms = new System.IO.MemoryStream();
            ms.Write(BitConverter.GetBytes(id), 0, 2);       // ID
            ms.Write(new byte[] { 0x01, 0x00 }, 0, 2);       // flags: recursion desired
            ms.Write(new byte[] { 0x00, 0x01 }, 0, 2);       // QDCOUNT
            ms.Write(new byte[] { 0x00, 0x00, 0, 0, 0, 0 }, 0, 6); // AN/NS/AR = 0
            foreach (var part in host.Split('.'))
            {
                byte[] b = Encoding.ASCII.GetBytes(part);
                ms.WriteByte((byte)b.Length);
                ms.Write(b, 0, b.Length);
            }
            ms.WriteByte(0);
            ms.Write(new byte[] { 0x00, 0x01, 0x00, 0x01 }, 0, 4); // QTYPE=A, QCLASS=IN
            return ms.ToArray();
        }

        private static void ParseAnswers(byte[] resp, List<string> into)
        {
            if (resp.Length < 12) return;
            int qd = Be16(resp, 4);
            int an = Be16(resp, 6);
            int p = 12;
            for (int i = 0; i < qd; i++)
            {
                p = SkipName(resp, p);
                p += 4;
            }
            for (int i = 0; i < an && p < resp.Length; i++)
            {
                p = SkipName(resp, p);
                if (p + 10 > resp.Length) return;
                int type = Be16(resp, p);
                int cls = Be16(resp, p + 2);
                int rdlen = Be16(resp, p + 8);
                p += 10;
                if (type == 1 && cls == 1 && rdlen == 4 && p + 4 <= resp.Length)
                {
                    string ip = string.Format("{0}.{1}.{2}.{3}",
                        resp[p], resp[p + 1], resp[p + 2], resp[p + 3]);
                    if (!into.Contains(ip)) into.Add(ip);
                }
                p += rdlen;
            }
        }

        private static int Be16(byte[] b, int p)
        {
            return (b[p] << 8) | b[p + 1];
        }

        private static int SkipName(byte[] b, int p)
        {
            while (p < b.Length)
            {
                byte len = b[p];
                if (len == 0) return p + 1;
                if ((len & 0xC0) == 0xC0) return p + 2; // 压缩指针
                p += len + 1;
            }
            return p;
        }

        /// <summary>TCP 握手测速（3 次取最好），返回 ms，失败 -1</summary>
        private static int TcpPing(string ip, int port, int timeoutMs)
        {
            return MeasureConnect(ip, port, timeoutMs);
        }

        private static int MeasureConnect(string ip, int port, int timeoutMs)
        {
            int best = -1;
            for (int i = 0; i < 3; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    using (var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                    {
                        sock.ReceiveTimeout = timeoutMs;
                        var ar = sock.BeginConnect(IPAddress.Parse(ip), port, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                        {
                            sock.Close();
                            continue;
                        }
                        sock.EndConnect(ar);
                        int ms = (int)((Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency);
                        if (best < 0 || ms < best) best = ms;
                    }
                }
                catch { }
            }
            return best;
        }
    }
}
