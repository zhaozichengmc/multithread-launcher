using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PclDl
{
    /// <summary>
    /// 下载状态
    /// </summary>
    public enum DlState
    {
        Idle,
        Probing,
        Downloading,
        Paused,
        Merging,
        Finished,
        Failed,
        Cancelled
    }

    /// <summary>
    /// 进度快照（供界面显示）
    /// </summary>
    public class DlProgress
    {
        public DlState State;
        public long Done;
        public long Total;
        public long Speed;      // B/s
        public int ActiveThreads;
        public int MaxThreads;
        public string Message;
        public bool RangeSupported;
    }

    /// <summary>
    /// 一段下载区间。算法取自 PCL2 (Plain Craft Launcher 2) 的 NetFile / NetThread：
    /// 每个线程只负责 [Start, End] 这一段，各自写独立临时文件，最后按顺序拼接。
    /// 为了支持"大线程继续干活时被切分"，End 会在运行中被动态改小。
    /// </summary>
    internal class Segment
    {
        public long Start;
        public long End;          // 闭区间，含 End（用 Interlocked.Read 读取）
        public long Done;         // 已写入字节（Interlocked）
        public string TempPath;
        public int Index;

        /// <summary>该分块当前使用的源（主源或镜像），各分块独立，避免混源</summary>
        public string Url;
        /// <summary>0 表示主源，1..N 表示第 N 个镜像</summary>
        public int SourceIndex;

        /// <summary>该分块是否有线程在跑（暂停没收干净时不重复开线程）</summary>
        public volatile bool Alive;

        public long Length
        {
            get { return Interlocked.Read(ref End) - Start + 1; }
        }

        public long Remaining
        {
            get { return Length - Interlocked.Read(ref Done); }
        }
    }

    /// <summary>内部信号：收到暂停请求，正常收尾而不是当成错误</summary>
    internal class PauseSignal : Exception
    {
        public PauseSignal() : base("已暂停") { }
    }

    /// <summary>
    /// 多线程分块下载器（PCL2 下载算法的独立实现）
    /// </summary>
    public class MultiThreadDownloader
    {
        // PCL2 中 FilePieceLimit = 262144：小于 256KB 的剩余数据不值得再开线程
        private const long PieceLimit = 262144;
        private const long NoSplitSize = 1048576;   // 小于 1MB 不分块
        private const int BufferSize = 16384;
        private const int ReadTimeoutMs = 30000;

        public string Url;
        public string SavePath;
        public int MaxThreads = 16;
        public bool SimulateBrowserHeaders = true;  // 带浏览器 UA，部分站点会校验

        /// <summary>额外请求头（Referer / Cookie / User-Agent / Authorization 等）</summary>
        public Dictionary<string, string> CustomHeaders = new Dictionary<string, string>();

        /// <summary>代理；null 表示跟随系统</summary>
        public ProxyConfig Proxy;

        /// <summary>备用镜像地址，主源失败后自动轮换（按分块各自轮换）</summary>
        public List<string> Mirrors = new List<string>();

        /// <summary>期望的 SHA-256（小写十六进制）；填了就校验</summary>
        public string ExpectedSha256;

        /// <summary>不论有没有期望值都计算 SHA-256</summary>
        public bool AlwaysHash;

        public string ActualSha256;
        public long PeakSpeed;

        public event Action<string> OnLog;
        public event Action<DlProgress> OnProgress;

        private volatile IHttpTransport _http;

        /// <summary>启用 DNS 优选（自建 Socket 通道，按最优 DNS 解析出的 IP 连接）</summary>
        public bool OptimizeDns = true;

        private long _totalSize;
        private bool _rangeSupported;
        private bool _unknownSize;

        private readonly List<Segment> _segs = new List<Segment>();
        private readonly object _segLock = new object();
        private readonly List<Thread> _workers = new List<Thread>();

        private volatile bool _pause;
        private volatile bool _cancel;
        private volatile bool _fatal;
        private volatile string _fatalMsg;
        private int _recoverRounds;
        private volatile int _active;

        private long _speed;
        private Thread _runThread;
        private volatile bool _supervisorAlive;
        private volatile bool _singleMode;
        private long _singleDone;

        /// <summary>探测后确定的主源（镜像切换时以它为基准）</summary>
        private string _baseUrl;

        /// <summary>取消时是否删掉已下载的分块/临时文件。程序退出前置 false，下次可续传</summary>
        public bool CancelDeletesFiles = true;

        public DlState State { get; private set; }

        /// <summary>删掉本任务留下的临时文件与断点状态（把任务从队列移除时调用）</summary>
        public void DiscardPartial()
        {
            try { CleanUp(true); } catch { }
        }

        // 单调时钟：Environment.TickCount 49.7 天会溢出成负数，速度和超时全乱
        private static readonly long ClockBase = Stopwatch.GetTimestamp();
        private static long Now()
        {
            return (long)((Stopwatch.GetTimestamp() - ClockBase) * (1000.0 / Stopwatch.Frequency));
        }

        public MultiThreadDownloader(string url, string savePath, int maxThreads, bool optimizeDns = true)
        {
            Url = url;
            _baseUrl = url;
            SavePath = savePath;
            MaxThreads = Math.Max(1, Math.Min(256, maxThreads));
            OptimizeDns = optimizeDns;
            State = DlState.Idle;
        }

        private void Log(string msg)
        {
            var h = OnLog;
            if (h != null) h(msg);
        }

        private void Report(DlState st, string msg)
        {
            State = st;
            if (_speed > PeakSpeed) PeakSpeed = _speed;
            var h = OnProgress;
            if (h == null) return;
            h(new DlProgress
            {
                State = st,
                Done = _singleMode ? Interlocked.Read(ref _singleDone) : TotalDone(),
                Total = _totalSize,
                Speed = _speed,
                ActiveThreads = _active,
                MaxThreads = MaxThreads,
                Message = msg,
                RangeSupported = _rangeSupported
            });
        }

        private long TotalDone()
        {
            long sum = 0;
            lock (_segLock)
            {
                foreach (var s in _segs) sum += Interlocked.Read(ref s.Done);
            }
            return sum;
        }

        private void BuildClients()
        {
#if NETFRAMEWORK
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }
#endif
            _http = new HttpClientTransport();
            Configure(_http);

            if (!OptimizeDns)
            {
                if (Proxy != null && Proxy.Valid && Proxy.Socks5)
                {
                    // 系统 HttpClient 不支持 SOCKS5，必须走自建 Socket 通道
                    _http = new SocketTransport(false, Log);
                    Configure(_http);
                    Log("SOCKS5 代理：使用自建 Socket 通道");
                }
                else Log("DNS 优选：关闭，使用系统 DNS");
                return;
            }

            // 有系统代理时不能绕过它直连，否则会连不上
            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                Uri pu = proxy != null ? proxy.GetProxy(new Uri(Url)) : null;
                if (pu != null && pu.ToString() != Url)
                {
                    Log("检测到系统代理，走代理通道（不做 DNS 优选）");
                    return;
                }
            }
            catch { }

            _http = new SocketTransport(true, Log);
            Configure(_http);
            Log("DNS 优选：启用（自建 Socket 通道）");
        }

        private void Configure(IHttpTransport t)
        {
            if (t == null) return;
            var hc = t as HttpClientTransport;
            if (hc != null) { hc.Headers = CustomHeaders; hc.Proxy = Proxy; }
            var sk = t as SocketTransport;
            if (sk != null) { sk.Headers = CustomHeaders; sk.Proxy = Proxy; }
        }

        private void FallbackTransport(string why)
        {
            if (_http is SocketTransport)
            {
                if (Proxy != null && Proxy.Valid && Proxy.Socks5)
                {
                    // HttpClient 只认 HTTP 代理，切成它就等于把 SOCKS5 当成 HTTP 代理连，越换越坏
                    Log("SOCKS5 通道出错（" + why + "），保持自建通道继续重试");
                    return;
                }
                _http = new HttpClientTransport();
                Configure(_http);
                Log("DNS 优选通道不可用（" + why + "），已回退系统通道");
            }
        }

        // ================= 对外接口 =================

        public void Start()
        {
            if (_runThread != null) return;
            BuildClients();
            _supervisorAlive = true;
            _runThread = new Thread(Run);
            _runThread.IsBackground = true;
            _runThread.Name = "下载调度";
            _runThread.Start();
        }

        public void Pause()
        {
            if (State != DlState.Downloading && State != DlState.Probing) return;
            _pause = true;
            Log("收到暂停请求，等待线程收尾…");
        }

        public void Resume()
        {
            if (State != DlState.Paused) return;
            // 调度线程可能还没真正退出（刚报完 Paused 就点继续），再起一条会两个调度器抢同一批分块
            if (_supervisorAlive) { Log("调度线程还在收尾，请稍后再点继续"); return; }
            _pause = false;
            _supervisorAlive = true;
            _runThread = new Thread(ResumeRun);
            _runThread.IsBackground = true;
            _runThread.Name = "下载调度";
            _runThread.Start();
        }

        public void Cancel()
        {
            _cancel = true;
            _pause = false;
            Log("收到取消请求…");
        }

        // ================= 主流程 =================

        private void EnsureDir()
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(SavePath));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                throw new Exception("无法创建保存目录：" + ex.Message);
            }
        }

        private void Run()
        {
            try
            {
                EnsureDir();
                Report(DlState.Probing, "正在探测文件大小…");
                Probe();
                _baseUrl = Url;

                if (_cancel) { CleanUp(CancelDeletesFiles); Report(DlState.Cancelled, "已取消"); return; }

                if (!_rangeSupported || _unknownSize || _totalSize < NoSplitSize || MaxThreads == 1)
                {
                    Log(_unknownSize
                        ? "服务器未给出文件大小，使用单线程下载"
                        : (!_rangeSupported ? "服务器不支持分段（无 206 响应），使用单线程下载"
                                            : "文件小于 1MB，单线程直接下载"));
                    ClearState();          // 单线程走 .part 续传，不用分块状态
                    _singleMode = true;
                    SingleThreadDownload();
                    return;
                }

                Log(string.Format("文件大小 {0}，服务器支持分段，最多 {1} 线程", FormatSize(_totalSize), MaxThreads));

                if (TryLoadState())
                {
                    Log("发现上次没下完的分块，已恢复 " + _segs.Count + " 段，接着下…");
                }
                else
                {
                    ClearState();
                    CreateFirstSegment();
                }

                Report(DlState.Downloading, "开始下载…");

                // 恢复出来可能有多段，未完成且没有线程在跑的都拉起来
                lock (_segLock)
                {
                    foreach (var s in _segs)
                        if (s.Remaining > 0 && !s.Alive) SpawnWorker(s);
                }
                Supervisor();
            }
            catch (Exception ex)
            {
                if (_cancel) { CleanUp(CancelDeletesFiles); Report(DlState.Cancelled, "已取消"); }
                else { CleanUp(false); Log("失败：" + ex.Message); Report(DlState.Failed, ex.Message); }
            }
            finally { _supervisorAlive = false; }
        }

        private void ResumeRun()
        {
            try
            {
                if (_singleMode)
                {
                    Report(DlState.Downloading, "继续下载…");
                    SingleThreadDownload();
                    return;
                }
                Report(DlState.Downloading, "继续下载…");
                lock (_segLock)
                {
                    foreach (var s in _segs)
                    {
                        if (s.Remaining <= 0) continue;
                        if (s.Alive) continue;   // 上一轮没收干净的线程还在跑，绝不能再开一条写同一个文件
                        SpawnWorker(s);
                    }
                }
                Supervisor();
            }
            catch (Exception ex)
            {
                if (_cancel) { CleanUp(CancelDeletesFiles); Report(DlState.Cancelled, "已取消"); }
                else { CleanUp(false); Log("失败：" + ex.Message); Report(DlState.Failed, ex.Message); }
            }
            finally { _supervisorAlive = false; }
        }

        /// <summary>
        /// 探测：用 Range: bytes=0-0 请求，从 206 / Content-Range 判断是否支持分段
        /// </summary>
        private void Probe()
        {
            FetchResult resp = null;
            try
            {
                resp = FetchAny(0, 0);
            }
            catch (Exception ex)
            {
                FallbackTransport(ex.Message);
                resp = FetchAny(null, null);
            }

            using (resp)
            {
                Log("HTTP " + resp.Status + " " + resp.Reason);

                if (string.IsNullOrEmpty(SavePath) || Directory.Exists(SavePath))
                {
                    string name = GetFileName(resp, resp.FinalUrl ?? Url);
                    string dir = string.IsNullOrEmpty(SavePath) ? Environment.CurrentDirectory : SavePath;
                    SavePath = Path.Combine(dir, name);
                }

                if (resp.Status == 206)
                {
                    _rangeSupported = true;
                    _totalSize = resp.TotalLength ?? resp.ContentLength;
                }
                else if (resp.Status == 200)
                {
                    _rangeSupported = false;
                    _totalSize = resp.ContentLength;
                }
                else
                {
                    throw new Exception("服务器返回 " + resp.Status + " " + resp.Reason);
                }

                if (_totalSize <= 0)
                {
                    _unknownSize = true;
                    _totalSize = 0;
                }
            }

            Log("保存到：" + SavePath);
        }

        private FetchResult FetchAny(long? from, long? to)
        {
            Exception last = null;
            var list = new List<string>();
            list.Add(Url);
            list.AddRange(Mirrors);

            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    var r = _http.Fetch(list[i], from, to);
                    if (r.Status >= 400 && i < list.Count - 1)
                    {
                        Log("源不可用（HTTP " + r.Status + "）：" + list[i]);
                        r.Dispose();
                        continue;
                    }
                    if (i > 0)
                    {
                        Log("主源不可用，改用镜像：" + list[i]);
                        Url = list[i];
                    }
                    return r;
                }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new Exception("没有可用地址");
        }

        private static string GetFileName(FetchResult resp, string url)
        {
            string cd = resp.ContentDisposition;
            if (!string.IsNullOrEmpty(cd))
            {
                string n = cd.Trim('"');
                n = Path.GetFileName(n.Replace("\\", "/"));
                if (n.Length > 0) return Sanitize(n);
            }
            try
            {
                var uri = new Uri(url);
                string last = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
                if (!string.IsNullOrEmpty(last)) return Sanitize(last);
            }
            catch { }
            return "download.bin";
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            // Windows 不允许文件名以点或空格结尾，也禁止 CON/NUL 这类设备名
            name = name.TrimEnd('.', ' ');
            if (name.Length == 0) return "download.bin";
            string stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            foreach (var r in reserved)
                if (stem == r) return "_" + name;
            if (name.Length > 200) name = name.Substring(0, 200);
            return name;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "?";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? ((long)v).ToString() : v.ToString("0.0")) + " " + u[i];
        }

        // ================= 分块与调度 =================

        private void CreateFirstSegment()
        {
            var s = new Segment
            {
                Start = 0,
                End = _totalSize - 1,
                Index = 1,
                TempPath = TempPathFor(1),
                Url = _baseUrl,
                SourceIndex = 0
            };
            string dir = Path.GetDirectoryName(SavePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.GetDirectoryName(s.TempPath));
            File.WriteAllBytes(s.TempPath, new byte[0]);
            lock (_segLock) { _segs.Add(s); }
            SaveState();
        }

        private static string TempDir()
        {
            return Path.Combine(Path.GetTempPath(), "PclDlCache");
        }

        /// <summary>同一保存路径的临时文件名字固定，重启后才能接着下</summary>
        private string OwnTempDir()
        {
            string full = SavePath ?? "";
            try { full = Path.GetFullPath(full); } catch { }
            using (var md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return Path.Combine(TempDir(), sb.ToString());
            }
        }

        private string TempPathFor(int idx)
        {
            return Path.Combine(OwnTempDir(), idx + ".part");
        }

        // ---------- 断点状态：分块进度落盘，重启 / 取消后能接着下 ----------

        private string StatePath { get { return (SavePath ?? "") + ".pdlstate"; } }

        private void SaveState()
        {
            try
            {
                if (string.IsNullOrEmpty(SavePath)) return;
                var sb = new StringBuilder();
                List<Segment> segs;
                lock (_segLock) { segs = new List<Segment>(_segs); }
                sb.Append("PDL1\t").Append(_totalSize).Append('\t').Append(_rangeSupported ? 1 : 0)
                  .Append('\t').Append(_baseUrl ?? Url).Append("\r\n");
                foreach (var s in segs)
                {
                    sb.Append(s.Start).Append('\t')
                      .Append(Interlocked.Read(ref s.End)).Append('\t')
                      .Append(Interlocked.Read(ref s.Done)).Append('\t')
                      .Append(s.Url ?? _baseUrl).Append('\t')
                      .Append(s.TempPath).Append("\r\n");
                }
                string statePath = StatePath;
                string dir = Path.GetDirectoryName(statePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = statePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(statePath)) File.Delete(statePath);
                File.Move(tmp, statePath);
            }
            catch { }
        }

        private void ClearState()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
            try
            {
                string d = OwnTempDir();
                if (Directory.Exists(d))
                    foreach (var f in Directory.GetFiles(d)) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }

        private bool TryLoadState()
        {
            try
            {
                if (!File.Exists(StatePath)) return false;
                var lines = File.ReadAllLines(StatePath, Encoding.UTF8);
                if (lines.Length < 2) return false;
                var head = lines[0].Split('\t');
                if (head.Length < 4 || head[0] != "PDL1") return false;
                long total;
                if (!long.TryParse(head[1], out total) || total != _totalSize) return false;

                var list = new List<Segment>();
                long expectStart = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    var p = lines[i].Split('\t');
                    if (p.Length < 5) return false;
                    long st, en, dn;
                    if (!long.TryParse(p[0], out st) || !long.TryParse(p[1], out en) || !long.TryParse(p[2], out dn)) return false;
                    if (st != expectStart || en < st || dn < 0 || dn > en - st + 1) return false;
                    string temp = p[4];
                    long len;
                    if (!File.Exists(temp))
                    {
                        // 还没开始写的分块本来就没有临时文件；以前这里直接 return false，
                        // 只要有一段没落地，整份进度就被丢掉重下。
                        if (dn > 0) return false;
                        try
                        {
                            string td = Path.GetDirectoryName(temp);
                            if (!string.IsNullOrEmpty(td)) Directory.CreateDirectory(td);
                            File.WriteAllBytes(temp, new byte[0]);
                        }
                        catch { return false; }
                        len = 0;
                    }
                    else len = new FileInfo(temp).Length;

                    if (len < dn)
                    {
                        // 文件比计数短：说明上次写完还没来得及计数就中断了，按实际长度算进度（前缀一定有效）
                        dn = len;
                    }
                    else if (len > dn)
                    {
                        // 上次写多了（写完没来得及计数），截到计数位置，避免重复字节
                        using (var fs = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.Read))
                            fs.SetLength(dn);
                    }
                    list.Add(new Segment
                    {
                        Start = st, End = en, Done = dn, TempPath = temp,
                        Index = i, Url = p[3], SourceIndex = 0
                    });
                    expectStart = en + 1;
                }
                if (list.Count == 0 || expectStart != _totalSize) return false;

                lock (_segLock)
                {
                    _segs.Clear();
                    _segs.AddRange(list);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 调度循环：每隔 100ms 检查一次，若还有大块未下载数据就再切一刀开新线程。
        /// 切分规则照搬 PCL2：从"剩余最多"的段里，从尾部切走 40%，且不低于 256KB。
        /// </summary>
        private void Supervisor()
        {
            long lastSample = Now();
            long lastDone = TotalDone();
            long lastSaveState = Now();

            while (true)
            {
                if (AllSegmentsDone()) break;

                if (_cancel)
                {
                    WaitWorkers();
                    // 用户点了取消，就算数据已经拉满也别再合并出文件：保持"取消=不留产物"的直觉
                    CleanUp(CancelDeletesFiles);
                    Report(DlState.Cancelled, "已取消");
                    return;
                }

                if (_pause)
                {
                    WaitWorkers();
                    if (_cancel) { CleanUp(CancelDeletesFiles); Report(DlState.Cancelled, "已取消"); return; }
                    UpdateSpeedSampling(ref lastSample, ref lastDone);
                    _speed = 0;
                    SaveState();
                    Report(DlState.Paused, "已暂停，可继续");
                    return;
                }

                if (_fatal && _active == 0 && !AllSegmentsDone())
                {
                    WaitWorkers();
                    if (_recoverRounds < 2)
                    {
                        _recoverRounds++;
                        _fatal = false;
                        _fatalMsg = null;
                        Log("有分块失败，重新拉起未完成分块（第 " + _recoverRounds + " 次）…");
                        lock (_segLock)
                        {
                            foreach (var s in _segs)
                                if (s.Remaining > 0 && !s.Alive)
                                    SpawnWorker(s);
                        }
                        continue;
                    }
                    throw new Exception(_fatalMsg ?? "所有下载线程已退出，数据不完整");
                }

                // 高线程数时一次多切几刀，否则 100ms 才开一条线程，256 线程要等到天荒地老
                for (int i = 0; i < 12; i++)
                {
                    if (!TrySplitOne()) break;
                }
                UpdateSpeedSampling(ref lastSample, ref lastDone);
                Report(DlState.Downloading, null);

                if (Now() - lastSaveState >= 1000)
                {
                    lastSaveState = Now();
                    SaveState();
                }

                Thread.Sleep(100);
            }

            WaitWorkers();

            if (!AllSegmentsDone())
            {
                if (_cancel) { CleanUp(CancelDeletesFiles); Report(DlState.Cancelled, "已取消"); return; }
                throw new Exception(_fatalMsg ?? "分块数据不完整");
            }

            Report(DlState.Merging, "正在合并分块…");
            Merge();
            Report(DlState.Finished, "下载完成");
            Log("下载完成：" + SavePath);
        }

        private bool AllSegmentsDone()
        {
            lock (_segLock)
            {
                if (_segs.Count == 0) return false;
                foreach (var s in _segs)
                    if (s.Remaining > 0) return false;
            }
            return true;
        }

        private void UpdateSpeedSampling(ref long lastTime, ref long lastDone)
        {
            long now = Now();
            if (now - lastTime >= 200)
            {
                long done = TotalDone();
                _speed = (long)((done - lastDone) * 1000.0 / Math.Max(1, now - lastTime));
                lastTime = now;
                lastDone = done;
            }
        }

        private bool TrySplitOne()
        {
            if (_active >= MaxThreads) return false;
            if (_cancel || _pause) return false;

            Segment target = null;
            Segment seg = null;
            lock (_segLock)
            {
                foreach (var s in _segs)
                {
                    if (s.Remaining <= 0) continue;
                    if (s.Remaining < PieceLimit) continue;
                    if (target == null || s.Remaining > target.Remaining) target = s;
                }
                if (target == null) return false;

                long remaining = target.Remaining;
                long take = Math.Max(PieceLimit, (long)Math.Round(remaining * 0.4));
                long oldEnd = Interlocked.Read(ref target.End);
                long newStart = oldEnd + 1 - take;
                if (newStart - target.Start < PieceLimit) return false; // 切完太小，不切
                long newEnd = newStart - 1;

                seg = new Segment
                {
                    Start = newStart,
                    End = oldEnd,
                    Index = _segs.Count + 1,
                    TempPath = TempPathFor(_segs.Count + 1),
                    Url = target.Url,
                    SourceIndex = target.SourceIndex
                };
                File.WriteAllBytes(seg.TempPath, new byte[0]);
                _segs.Add(seg);
                Interlocked.Exchange(ref target.End, newEnd);
                Log(string.Format("切分新线程 #{0}：{1} ~ {2}（原线程止于 {3}）",
                    seg.Index, seg.Start, seg.End, newEnd));
                SpawnWorker(seg);
            }
            return true;
        }

        private void SpawnWorker(Segment seg)
        {
            var t = new Thread(() => WorkerMain(seg));
            t.IsBackground = true;
            t.Name = "下载线程 #" + seg.Index;
            lock (_segLock) { _workers.Add(t); }
            seg.Alive = true;
            Interlocked.Increment(ref _active);
            t.Start();
        }

        /// <summary>等所有下载线程真正退出：暂停后立刻继续会变成两条线程写同一个分块</summary>
        private void WaitWorkers()
        {
            long deadline = Now() + 60000;
            while (_active > 0 && Now() < deadline)
                Thread.Sleep(100);

            List<Thread> list;
            lock (_segLock) { list = new List<Thread>(_workers); }
            foreach (var t in list)
            {
                try { if (t.IsAlive) t.Join(200); } catch { }
            }
        }

        /// <summary>
        /// 单个线程：对 [Start+Done, End] 发 Range 请求，写入自己的临时文件；失败自动续传重试。
        /// </summary>
        private void WorkerMain(Segment seg)
        {
            int attempt = 0;
            int switchCount = 0;
            try
            {
                while (seg.Remaining > 0)
                {
                    if (_pause || _cancel) return;
                    try
                    {
                        DownloadSegmentOnce(seg);
                        attempt = 0;
                    }
                    catch (PauseSignal) { return; }
                    catch (Exception ex)
                    {
                        if (_pause || _cancel) return;
                        attempt++;

                        // 只换当前分块的源，别的分块继续用自己的源，避免混源拼出坏文件
                        if (attempt >= 2 && switchCount < Mirrors.Count)
                        {
                            switchCount++;
                            seg.SourceIndex++;
                            seg.Url = seg.SourceIndex <= Mirrors.Count
                                ? Mirrors[seg.SourceIndex - 1]
                                : _baseUrl;
                            attempt = 0;
                            Log("线程 #" + seg.Index + " 切换镜像源：" + seg.Url);
                            continue;
                        }

                        if (attempt > 3) throw;
                        Log(string.Format("线程 #{0} 第 {1} 次失败：{2}，{3} 毫秒后续传", seg.Index, attempt, ex.Message, 1000 * attempt));
                        Thread.Sleep(1000 * attempt);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_pause && !_cancel)
                {
                    Log(string.Format("线程 #{0} 放弃：{1}", seg.Index, ex.Message));
                    _fatal = true;
                    _fatalMsg = "线程 #" + seg.Index + " 多次重试失败：" + ex.Message;
                }
            }
            finally
            {
                seg.Alive = false;
                Interlocked.Decrement(ref _active);
            }
        }

        /// <summary>读一段，边读边看暂停/取消/超时，暂停能秒级响应而不是卡 30 秒</summary>
        private int ReadChunk(Stream stream, byte[] buf, int want)
        {
            var task = stream.ReadAsync(buf, 0, want);
            long start = Now();
            while (true)
            {
                try
                {
                    if (task.Wait(100)) break;
                }
                catch (AggregateException ex)
                {
                    throw (ex.InnerException ?? ex);
                }
                if (_cancel) throw new OperationCanceledException("已取消");
                if (_pause) throw new PauseSignal();
                if (Now() - start > ReadTimeoutMs)
                    throw new Exception("读取超时（30 秒无数据）");
            }
            try { return task.Result; }
            catch (AggregateException ex) { throw (ex.InnerException ?? ex); }
        }

        private void DownloadSegmentOnce(Segment seg)
        {
            long done = Interlocked.Read(ref seg.Done);
            long from = seg.Start + done;
            long to = Interlocked.Read(ref seg.End);
            if (from > to) return;

            // 对齐临时文件长度：上次可能写完但计数没加上，直接 append 会重复字节
            if (File.Exists(seg.TempPath))
            {
                long len = new FileInfo(seg.TempPath).Length;
                if (len > done)
                {
                    using (var fs = new FileStream(seg.TempPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                        fs.SetLength(done);
                }
                else if (len < done)
                {
                    Interlocked.Exchange(ref seg.Done, len);
                    done = len;
                    from = seg.Start + done;
                    if (from > to) return;
                }
            }
            else File.WriteAllBytes(seg.TempPath, new byte[0]);

            FetchResult resp;
            var srcUrl = seg.Url ?? _baseUrl;
            try
            {
                resp = _http.Fetch(srcUrl, from, to);
            }
            catch (Exception ex)
            {
                FallbackTransport(ex.Message);
                throw;
            }

            using (resp)
            {
                bool fullBody = false;
                if (resp.Status != 206)
                {
                    // 很多服务器对"bytes=0-整份"的请求直接回 200 全量（尤其 CDN/OSS）。
                    // 只要请求的正好是完整文件，就当成整份下载，不要误判成"不支持分段"。
                    long segEnd0 = Interlocked.Read(ref seg.End);
                    if (resp.Status == 200 && from == 0 && _totalSize > 0 && segEnd0 == _totalSize - 1)
                        fullBody = true;
                    else if (resp.Status == 200)
                        throw new Exception("服务器忽略了 Range 请求（返回 200），该地址无法分块下载");
                    else
                        throw new Exception("HTTP " + resp.Status);
                }
                long expect = to - from + 1;
                long gotLen = resp.ContentLength;
                if (!fullBody && gotLen > 0 && gotLen != expect && from == seg.Start && expect == _totalSize)
                    throw new Exception(string.Format("文件大小与探测不一致：期望 {0}，实际 {1}", expect, gotLen));

                var stream = resp.Body;
                var fs = new FileStream(seg.TempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                try
                {
                    fs.SetLength(done);
                    fs.Position = done;

                    byte[] buf = new byte[BufferSize];
                    long lastRecv = Now();
                    while (seg.Remaining > 0)
                    {
                        if (_cancel) throw new OperationCanceledException("已取消");
                        if (_pause) throw new PauseSignal();

                        long room = seg.Remaining;
                        int want = (int)Math.Min(BufferSize, room);
                        if (want <= 0) break;

                        int n = ReadChunk(stream, buf, want);
                        if (n <= 0) break;

                        long room2 = seg.Remaining;
                        if (room2 <= 0) break;
                        if (n > room2) n = (int)room2;

                        fs.Write(buf, 0, n);
                        Interlocked.Add(ref seg.Done, n);
                        SpeedLimiter.Global.Take(n);

                        long gap = Now() - lastRecv;
                        if (gap > 5000 && n < gap) throw new Exception("速度过慢，主动断开重连");
                        lastRecv = Now();
                    }
                    fs.Flush();
                }
                finally
                {
                    fs.Dispose();
                    stream.Dispose();
                }
            }
        }

        // ================= 单线程兜底 =================
        private void SingleThreadDownload()
        {
            string dir = Path.GetDirectoryName(SavePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string part = SavePath + ".part";
            long done = 0;
            if (File.Exists(part)) done = new FileInfo(part).Length;
            if (done > 0 && !_rangeSupported) done = 0;
            Interlocked.Exchange(ref _singleDone, done);

            while (done < _totalSize || _unknownSize)
            {
                if (_cancel) { CleanUp(CancelDeletesFiles); Report(DlState.Cancelled, "已取消"); return; }

                FetchResult resp;
                try
                {
                    resp = _http.Fetch(Url, _rangeSupported && done > 0 ? done : (long?)null, null);
                }
                catch (Exception ex)
                {
                    FallbackTransport(ex.Message);
                    throw;
                }

                using (resp)
                {
                    if (resp.Status != 200 && resp.Status != 206)
                        throw new Exception("HTTP " + resp.Status);

                    if (_unknownSize && resp.ContentLength > 0)
                        _totalSize = (_rangeSupported && done > 0) ? done + resp.ContentLength : resp.ContentLength;

                    if (done > 0 && File.Exists(part) && new FileInfo(part).Length > done)
                    {
                        using (var trim = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.Read))
                            trim.SetLength(done);
                    }

                    var stream = resp.Body;
                    var fs = new FileStream(part, done > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
                    try
                    {
                        byte[] buf = new byte[BufferSize];
                        long lastSample = Now(), lastDone = done;
                        while (true)
                        {
                            if (_cancel) { break; }
                            // 暂停要正常收尾：报 Paused 出去，界面才能再点继续
                            if (_pause) break;

                            int n = ReadChunk(stream, buf, BufferSize);
                            if (n <= 0) break;
                            fs.Write(buf, 0, n);
                            done += n;
                            Interlocked.Exchange(ref _singleDone, done);
                            SpeedLimiter.Global.Take(n);

                            long now = Now();
                            if (now - lastSample >= 200)
                            {
                                _speed = (long)((done - lastDone) * 1000.0 / Math.Max(1, now - lastSample));
                                lastSample = now; lastDone = done;
                                Report(DlState.Downloading, null);
                            }
                            if (!_unknownSize && done >= _totalSize) break;
                        }
                        fs.Flush();
                    }
                    finally { fs.Dispose(); stream.Dispose(); }
                }

                if (_cancel) { CleanUp(CancelDeletesFiles); Report(DlState.Cancelled, "已取消"); return; }
                if (_pause) break;
                if (!_unknownSize && done >= _totalSize) break;
                if (!_rangeSupported) break;
            }

            if (_pause)
            {
                _speed = 0;
                Report(DlState.Paused, "已暂停（点继续会接着下）");
                return;
            }

            if (!_unknownSize && done < _totalSize)
            {
                try { File.Delete(part); } catch { }
                throw new Exception("服务器不支持断点续传，连接中断后未能下完");
            }

            if (File.Exists(SavePath)) File.Delete(SavePath);
            File.Move(part, SavePath);
            if (_unknownSize) _totalSize = done;
            try { VerifyFinished(); }
            catch
            {
                try { if (File.Exists(SavePath)) File.Delete(SavePath); } catch { }
                throw;
            }
            Report(DlState.Finished, "下载完成");
            Log("下载完成：" + SavePath);
        }

        // ================= 合并 =================

        private void Merge()
        {
            var ordered = new List<Segment>();
            lock (_segLock) { ordered.AddRange(_segs); }
            ordered.Sort((a, b) => a.Start.CompareTo(b.Start));

            // 覆盖检查：分段必须从 0 连续到末尾，缺一段就是状态坏了，不能拼出坏文件
            long cover = 0;
            foreach (var s in ordered)
            {
                if (s.Start != cover) throw new Exception("分块区间不连续（" + s.Start + " 应为 " + cover + "）");
                cover = Interlocked.Read(ref s.End) + 1;
            }
            if (_totalSize > 0 && cover != _totalSize)
                throw new Exception(string.Format("分块覆盖长度不对：{0} 应为 {1}", cover, _totalSize));

            string tmpOut = SavePath + ".merging";
            using (var outFs = new FileStream(tmpOut, FileMode.Create, FileAccess.Write))
            {
                foreach (var seg in ordered)
                {
                    long need = seg.Length;
                    using (var inFs = new FileStream(seg.TempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        byte[] buf = new byte[65536];
                        int n;
                        while (need > 0 && (n = inFs.Read(buf, 0, (int)Math.Min(buf.Length, need))) > 0)
                        {
                            outFs.Write(buf, 0, n);
                            need -= n;
                        }
                    }
                    if (need > 0) throw new Exception(string.Format("分块 #{0} 数据不足，缺少 {1} 字节", seg.Index, need));
                }
            }

            long finalLen = new FileInfo(tmpOut).Length;
            if (_totalSize > 0 && finalLen != _totalSize)
            {
                try { File.Delete(tmpOut); } catch { }
                throw new Exception(string.Format("合并后大小不一致：期望 {0}，实际 {1}", _totalSize, finalLen));
            }

            if (File.Exists(SavePath)) File.Delete(SavePath);
            File.Move(tmpOut, SavePath);
            try { VerifyFinished(); }
            catch
            {
                // 校验不过就删掉，别把坏文件留在用户目录里让人误以为下好了
                try { if (File.Exists(SavePath)) File.Delete(SavePath); } catch { }
                throw;
            }

            foreach (var seg in ordered)
            {
                try { if (File.Exists(seg.TempPath)) File.Delete(seg.TempPath); } catch { }
            }
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
            try
            {
                string own = OwnTempDir();
                if (Directory.Exists(own)) Directory.Delete(own, true);
            }
            catch { }
        }

        private void VerifyFinished()
        {
            if (string.IsNullOrEmpty(ExpectedSha256) && !AlwaysHash) return;

            try
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(SavePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var hash = sha.ComputeHash(fs);
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (var b in hash) sb.Append(b.ToString("x2"));
                    ActualSha256 = sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Log("计算 SHA-256 失败：" + ex.Message);
                return;
            }

            Log("SHA-256：" + ActualSha256);

            if (!string.IsNullOrEmpty(ExpectedSha256))
            {
                string want = ExpectedSha256.Trim().ToLowerInvariant().Replace(" ", "");
                if (want != ActualSha256)
                    throw new Exception("SHA-256 校验不匹配（期望 " + want.Substring(0, Math.Min(16, want.Length)) + "…）");
                Log("SHA-256 校验通过");
            }
        }

        private void CleanUp(bool deleteAll)
        {
            string part = SavePath + ".part";
            string merging = SavePath + ".merging";
            try { if (deleteAll && File.Exists(part)) File.Delete(part); } catch { }
            try { if (deleteAll && File.Exists(merging)) File.Delete(merging); } catch { }
            if (!deleteAll) return;
            lock (_segLock)
            {
                foreach (var s in _segs)
                {
                    try { if (File.Exists(s.TempPath)) File.Delete(s.TempPath); } catch { }
                }
                _segs.Clear();
            }
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
            try
            {
                string own = OwnTempDir();
                if (Directory.Exists(own)) Directory.Delete(own, true);
            }
            catch { }
        }
    }
}
