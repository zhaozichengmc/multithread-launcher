using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;

namespace PclDl
{
    /// <summary>一次 HTTP 请求的结果（只取头部 + 正文流）</summary>
    public class FetchResult : IDisposable
    {
        public int Status;
        public string Reason;
        public string FinalUrl;
        public long ContentLength = -1;
        public long? TotalLength;       // 来自 Content-Range
        public string ContentDisposition;
        public Stream Body;

        internal Socket Sock;

        public void Dispose()
        {
            try { if (Body != null) Body.Dispose(); } catch { }
            try { if (Sock != null) Sock.Close(); } catch { }
        }
    }

    public interface IHttpTransport
    {
        string Name { get; }
        /// <summary>from/to 为闭区间字节范围；to 为 null 表示到文件末尾</summary>
        FetchResult Fetch(string url, long? from, long? to);
    }

    // ===================== 基于系统栈（走代理、用系统 DNS） =====================

    public class HttpClientTransport : IHttpTransport
    {
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public ProxyConfig Proxy;

        public string Name { get { return "HTTP 客户端"; } }

        public FetchResult Fetch(string url, long? from, long? to)
        {
            ServicePointManager.DefaultConnectionLimit = 1024;
            ServicePointManager.UseNagleAlgorithm = false;
            ServicePointManager.Expect100Continue = false;

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = true
            };

            if (Proxy != null && Proxy.Valid)
            {
                handler.Proxy = new WebProxy(Proxy.Host, Proxy.Port);
                if (!string.IsNullOrEmpty(Proxy.User))
                    handler.Proxy.Credentials = new NetworkCredential(Proxy.User, Proxy.Pass);
                handler.UseProxy = true;
            }
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Timeout.Infinite) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("*/*");

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (from.HasValue)
                req.Headers.Range = new RangeHeaderValue(from.Value, to);

            foreach (var kv in Headers)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", kv.Value);
                    continue;
                }
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }

            var resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();

            var r = new FetchResult
            {
                Status = (int)resp.StatusCode,
                Reason = resp.ReasonPhrase,
                FinalUrl = resp.RequestMessage != null && resp.RequestMessage.RequestUri != null
                    ? resp.RequestMessage.RequestUri.ToString() : url,
                ContentLength = resp.Content.Headers.ContentLength.GetValueOrDefault(-1),
                Body = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            };

            var cr = resp.Content.Headers.ContentRange;
            if (cr != null && cr.Length.HasValue) r.TotalLength = cr.Length.Value;

            var cd = resp.Content.Headers.ContentDisposition;
            if (cd != null) r.ContentDisposition = cd.FileNameStar ?? cd.FileName;

            return r;
        }
    }

    // ===================== 自建 Socket 栈（可指定 DNS 解析出的 IP） =====================

    public class SocketTransport : IHttpTransport
    {
        private readonly bool _optimizeDns;
        private readonly Action<string> _log;

        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public ProxyConfig Proxy;

        public SocketTransport(bool optimizeDns, Action<string> log)
        {
            _optimizeDns = optimizeDns;
            _log = log;
        }

        public string Name { get { return "自建 Socket"; } }

        public FetchResult Fetch(string url, long? from, long? to)
        {
            string current = url;
            for (int hop = 0; hop < 6; hop++)
            {
                var uri = new Uri(current);
                bool tls = uri.Scheme == "https";
                int port = uri.Port > 0 ? uri.Port : (tls ? 443 : 80);

                bool viaProxy = Proxy != null && Proxy.Valid;
                string dialHost = viaProxy ? Proxy.Host : uri.DnsSafeHost;
                int dialPort = viaProxy ? Proxy.Port : port;

                var sock = Dial(dialHost, dialPort);
                Stream net = new NetworkStream(sock, false);

                if (viaProxy)
                {
                    if (Proxy.Socks5) Socks5Handshake(net, uri.DnsSafeHost, port, Proxy.User, Proxy.Pass);
                    else if (tls) HttpTunnel(net, uri.DnsSafeHost, port, Proxy.User, Proxy.Pass);
                }

                if (tls)
                {
                    var ssl = new SslStream(net, false);
                    ssl.AuthenticateAsClient(uri.DnsSafeHost); // SNI 与证书校验都按域名，IP 只是连接目标
                    net = ssl;
                }

                string path = uri.PathAndQuery;
                if (string.IsNullOrEmpty(path)) path = "/";

                bool absoluteForm = viaProxy && !Proxy.Socks5 && !tls;
                var sb = new StringBuilder();
                sb.Append("GET ").Append(absoluteForm ? uri.ToString() : path).Append(" HTTP/1.1\r\n");
                sb.Append("Host: ").Append(uri.IsDefaultPort ? uri.DnsSafeHost : uri.DnsSafeHost + ":" + port).Append("\r\n");
                sb.Append("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36\r\n");
                sb.Append("Accept: */*\r\n");
                sb.Append("Accept-Encoding: identity\r\n");
                if (from.HasValue)
                    sb.Append("Range: bytes=").Append(from.Value).Append('-')
                      .Append(to.HasValue ? to.Value.ToString() : "").Append("\r\n");
                if (absoluteForm && !string.IsNullOrEmpty(Proxy.User))
                    sb.Append("Proxy-Authorization: Basic ")
                      .Append(Convert.ToBase64String(Encoding.ASCII.GetBytes(Proxy.User + ":" + (Proxy.Pass ?? ""))))
                      .Append("\r\n");
                foreach (var kv in Headers)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (kv.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
                }
                sb.Append("Connection: close\r\n\r\n");

                byte[] reqBytes = Encoding.ASCII.GetBytes(sb.ToString());
                net.Write(reqBytes, 0, reqBytes.Length);
                net.Flush();

                var resp = ReadHead(net);
                resp.FinalUrl = current;

                if (resp.Status >= 300 && resp.Status < 400 && resp.Location != null)
                {
                    string next = new Uri(uri, resp.Location).ToString();
                    try { net.Dispose(); } catch { }
                    try { sock.Close(); } catch { }
                    current = next;
                    continue;
                }

                resp.Body = BuildBody(net, resp.ContentLength, resp.Chunked);
                resp.Sock = sock;
                return resp;
            }

            throw new Exception("重定向次数过多");
        }

        private Socket Dial(string host, int port)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                DnsChoice choice = _optimizeDns ? DnsOptimizer.Pick(host, port, _log) : null;
                if (choice != null) ip = IPAddress.Parse(choice.Ip);
                else
                {
                    var addrs = Dns.GetHostAddresses(host);
                    ip = null;
                    foreach (var a in addrs)
                        if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                    if (ip == null && addrs.Length > 0) ip = addrs[0];
                    if (ip == null) throw new Exception("域名解析失败：" + host);
                }
            }

            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sock.NoDelay = true;
            try { sock.ReceiveBufferSize = 2 * 1024 * 1024; } catch { }
            try { sock.SendBufferSize = 1024 * 1024; } catch { }
            ConnectWithTimeout(sock, ip, port, 12000);
            return sock;
        }

        private static void HttpTunnel(Stream net, string host, int port, string user, string pass)
        {
            var sb = new StringBuilder();
            sb.Append("CONNECT ").Append(host).Append(':').Append(port).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n");
            sb.Append("Proxy-Connection: keep-alive\r\n");
            if (!string.IsNullOrEmpty(user))
                sb.Append("Proxy-Authorization: Basic ")
                  .Append(Convert.ToBase64String(Encoding.ASCII.GetBytes(user + ":" + (pass ?? ""))))
                  .Append("\r\n");
            sb.Append("\r\n");
            byte[] b = Encoding.ASCII.GetBytes(sb.ToString());
            net.Write(b, 0, b.Length);
            net.Flush();

            string status = ReadLine(net);
            if (!status.Contains(" 200"))
                throw new Exception("代理拒绝 CONNECT：" + status);
            while (true)
            {
                string line = ReadLine(net);
                if (line.Length == 0) break;
            }
        }

        private static string ReadLine(Stream s)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = s.ReadByte()) >= 0)
            {
                if (b == 13) continue;
                if (b == 10) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        private static void Socks5Handshake(Stream net, string host, int port, string user, string pass)
        {
            bool auth = !string.IsNullOrEmpty(user);
            net.Write(new byte[] { 5, (byte)(auth ? 2 : 1), 0, (byte)(auth ? 2 : 0) }, 0, auth ? 4 : 3);
            net.Flush();

            byte[] reply = ReadExactly(net, 2);
            if (reply[0] != 5) throw new Exception("SOCKS5 握手失败");
            if (reply[1] == 2)
            {
                byte[] ub = Encoding.ASCII.GetBytes(user ?? "");
                byte[] pb = Encoding.ASCII.GetBytes(pass ?? "");
                var buf = new List<byte>();
                buf.Add(1);
                buf.Add((byte)ub.Length); buf.AddRange(ub);
                buf.Add((byte)pb.Length); buf.AddRange(pb);
                net.Write(buf.ToArray(), 0, buf.Count);
                net.Flush();
                byte[] ok = ReadExactly(net, 2);
                if (ok[1] != 0) throw new Exception("SOCKS5 用户名密码校验失败");
            }
            else if (reply[1] != 0) throw new Exception("SOCKS5 不支持该认证方式");

            IPAddress parsed;
            bool isIp = IPAddress.TryParse(host, out parsed);
            var req = new List<byte>();
            req.Add(5); req.Add(1); req.Add(0);
            if (isIp && parsed.AddressFamily == AddressFamily.InterNetwork)
            {
                req.Add(1); req.AddRange(parsed.GetAddressBytes());
            }
            else
            {
                byte[] hostBytes = Encoding.ASCII.GetBytes(host);
                req.Add(3); req.Add((byte)hostBytes.Length); req.AddRange(hostBytes);
            }
            req.Add((byte)(port >> 8)); req.Add((byte)(port & 0xFF));
            net.Write(req.ToArray(), 0, req.Count);
            net.Flush();

            byte[] head = ReadExactly(net, 4);
            if (head[1] != 0) throw new Exception("SOCKS5 连接被拒绝（代码 " + head[1] + "）");
            switch (head[3])
            {
                case 1: ReadExactly(net, 4 + 2); break;
                case 4: ReadExactly(net, 16 + 2); break;
                case 3:
                    int len = ReadExactly(net, 1)[0];
                    ReadExactly(net, len + 2);
                    break;
                default: throw new Exception("SOCKS5 返回了未知地址类型");
            }
        }

        private static byte[] ReadExactly(Stream s, int n)
        {
            byte[] b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) throw new Exception("连接被对端关闭");
                got += r;
            }
            return b;
        }

        private static void ConnectWithTimeout(Socket sock, IPAddress ip, int port, int timeoutMs)
        {
            var ar = sock.BeginConnect(ip, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                sock.Close();
                throw new Exception("连接超时：" + ip + ":" + port);
            }
            sock.EndConnect(ar);
        }

        private class HeadResult : FetchResult
        {
            public string Location;
            public bool Chunked;
        }

        private static HeadResult ReadHead(Stream net)
        {
            var buf = new List<byte>(4096);
            byte[] one = new byte[1];
            int total = 0;
            while (total < 16384)
            {
                int n = net.Read(one, 0, 1);
                if (n <= 0) throw new Exception("服务器提前断开了连接");
                buf.Add(one[0]);
                total++;
                int cnt = buf.Count;
                if (cnt >= 4 &&
                    buf[cnt - 1] == (byte)'\n' && buf[cnt - 2] == (byte)'\r' &&
                    buf[cnt - 3] == (byte)'\n' && buf[cnt - 4] == (byte)'\r')
                    break;
            }

            string head = Encoding.ASCII.GetString(buf.ToArray());
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) throw new Exception("响应为空");

            var statusParts = lines[0].Split(new[] { ' ' }, 3);
            var r = new HeadResult
            {
                Status = int.Parse(statusParts[1]),
                Reason = statusParts.Length > 2 ? statusParts[2] : ""
            };

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string k = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                string v = lines[i].Substring(colon + 1).Trim();

                if (k == "content-length") r.ContentLength = long.Parse(v);
                else if (k == "content-range")
                {
                    int slash = v.LastIndexOf('/');
                    if (slash >= 0)
                    {
                        string totalText = v.Substring(slash + 1);
                        if (totalText != "*") r.TotalLength = long.Parse(totalText);
                    }
                }
                else if (k == "location") r.Location = v;
                else if (k == "transfer-encoding" && v.ToLowerInvariant().Contains("chunked")) r.Chunked = true;
                else if (k == "content-disposition") r.ContentDisposition = v;
            }
            return r;
        }

        private static Stream BuildBody(Stream net, long length, bool chunked)
        {
            if (chunked) return new ChunkedStream(net);
            if (length >= 0) return new FixedLengthStream(net, length);
            return new EofStream(net);
        }
    }

    // ===================== 正文流包装 =====================

    internal abstract class BodyStreamBase : Stream
    {
        protected Stream Inner;
        public BodyStreamBase(Stream inner) { Inner = inner; }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { Inner.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    internal class FixedLengthStream : BodyStreamBase
    {
        private long _left;
        public FixedLengthStream(Stream inner, long len) : base(inner) { _left = len; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) return 0;
            int want = (int)Math.Min(count, _left);
            int n = Inner.Read(buffer, offset, want);
            if (n > 0) _left -= n;
            return n;
        }

        public override System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_left <= 0) return System.Threading.Tasks.Task.FromResult(0);
            int want = (int)Math.Min(count, _left);
            var t = Inner.ReadAsync(buffer, offset, want, ct);
            return ContinueAfter(t);
        }

        private async System.Threading.Tasks.Task<int> ContinueAfter(System.Threading.Tasks.Task<int> t)
        {
            int n = await t.ConfigureAwait(false);
            if (n > 0) _left -= n;
            return n;
        }
    }

    internal class EofStream : BodyStreamBase
    {
        public EofStream(Stream inner) : base(inner) { }
        public override int Read(byte[] buffer, int offset, int count) { return Inner.Read(buffer, offset, count); }
    }

    internal class ChunkedStream : BodyStreamBase
    {
        private long _chunkLeft;
        private bool _finished;

        public ChunkedStream(Stream inner) : base(inner) { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_finished) return 0;

            if (_chunkLeft == 0)
            {
                string line = ReadLine();
                if (line == null) { _finished = true; return 0; }
                int semi = line.IndexOf(';');
                if (semi >= 0) line = line.Substring(0, semi);
                _chunkLeft = Convert.ToInt64(line.Trim(), 16);
                if (_chunkLeft == 0)
                {
                    ReadLine(); // 末尾空行
                    _finished = true;
                    return 0;
                }
            }

            int want = (int)Math.Min(count, _chunkLeft);
            int n = Inner.Read(buffer, offset, want);
            if (n <= 0) { _finished = true; return 0; }
            _chunkLeft -= n;
            if (_chunkLeft == 0) ReadLine(); // 丢弃块尾 CRLF
            return n;
        }

        public override System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            return System.Threading.Tasks.Task.Run(() => Read(buffer, offset, count), ct);
        }

        private string ReadLine()
        {
            var sb = new StringBuilder();
            byte[] one = new byte[1];
            while (true)
            {
                int n = Inner.Read(one, 0, 1);
                if (n <= 0) return null;
                if (one[0] == (byte)'\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)one[0]);
                if (sb.Length > 1024) return sb.ToString();
            }
        }
    }
}
