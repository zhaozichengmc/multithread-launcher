using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

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

        public long Remaining
        {
            get { return Interlocked.Read(ref End) - Start + 1 - Interlocked.Read(ref Done); }
        }
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

        public string Url;
        public string SavePath;
        public int MaxThreads = 16;
        public bool SimulateBrowserHeaders = true;  // 带浏览器 UA，部分站点会校验

        /// <summary>额外请求头（Referer / Cookie / User-Agent / Authorization 等）</summary>
        public Dictionary<string, string> CustomHeaders = new Dictionary<string, string>();

        /// <summary>代理；null 表示跟随系统</summary>
        public ProxyConfig Proxy;

        /// <summary>备用镜像地址，主源失败后自动轮换</summary>
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
        private int _mirrorTries;
        private int _recoverRounds;
        private volatile int _active;

        private long _speed;
        private int _srcIdx;
        private long _doneSnapshot;

        private Thread _runThread;
        private volatile bool _singleMode;
        private long _singleDone;

        public DlState State { get; private set; }

        public MultiThreadDownloader(string url, string savePath, int maxThreads, bool optimizeDns = true)
        {
            Url = url;
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
            _runThread = new Thread(Run);
            _runThread.IsBackground = true;
            _runThread.Name = "下载调度";
            _runThread.Start();
        }

        public void Pause()
        {
            if (State != DlState.Downloading) return;
            _pause = true;
            Log("收到暂停请求，等待线程收尾…");
        }

        public void Resume()
        {
            if (State != DlState.Paused) return;
            _pause = false;
            _runThread = new Thread(ResumeRun);
            _runThread.IsBackground = true;
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

                if (_cancel) { CleanUp(true); Report(DlState.Cancelled, "已取消"); return; }

                if (!_rangeSupported || _unknownSize || _totalSize < NoSplitSize || MaxThreads == 1)
                {
                    Log(_unknownSize
                        ? "服务器未给出文件大小，使用单线程下载"
                        : (!_rangeSupported ? "服务器不支持分段（无 206 响应），使用单线程下载"
                                            : "文件小于 1MB，单线程直接下载"));
                    _singleMode = true;
                    SingleThreadDownload();
                    return;
                }

                Log(string.Format("文件大小 {0}，服务器支持分段，最多 {1} 线程", FormatSize(_totalSize), MaxThreads));
                Report(DlState.Downloading, "开始下载…");

                CreateFirstSegment();
                SpawnWorker(_segs[0]);
                Supervisor();
            }
            catch (Exception ex)
            {
                if (_cancel) { CleanUp(true); Report(DlState.Cancelled, "已取消"); }
                else { CleanUp(false); Log("失败：" + ex.Message); Report(DlState.Failed, ex.Message); }
            }
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
                        if (Interlocked.Read(ref s.Done) < s.End - s.Start + 1)
                            SpawnWorker(s);
                    }
                }
                Supervisor();
            }
            catch (Exception ex)
            {
                if (_cancel) { CleanUp(true); Report(DlState.Cancelled, "已取消"); }
                else { CleanUp(false); Log("失败：" + ex.Message); Report(DlState.Failed, ex.Message); }
            }
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
                string n = cd;
                if (!string.IsNullOrEmpty(n))
                {
                    n = n.Trim('"');
                    n = Path.GetFileName(n.Replace("\\", "/"));
                    if (n.Length > 0) return Sanitize(n);
                }
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
                TempPath = TempPathFor(1)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath) ?? ".");
            Directory.CreateDirectory(TempDir());
            File.WriteAllBytes(s.TempPath, new byte[0]);
            lock (_segLock) { _segs.Add(s); }
        }

        private static string TempDir()
        {
            return Path.Combine(Path.GetTempPath(), "PclDlCache");
        }

        private string TempPathFor(int idx)
        {
            string baseName = Path.GetFileName(SavePath ?? "download.bin");
            string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            return Path.Combine(TempDir(), string.Format("{0}.{1}.{2}.part", baseName, idx, tag));
        }

        /// <summary>
        /// 调度循环：每隔 100ms 检查一次，若还有大块未下载数据就再切一刀开新线程。
        /// 切分规则照搬 PCL2：从"剩余最多"的段里，从尾部切走 40%，且不低于 256KB。
        /// </summary>
        private void Supervisor()
        {
            long lastSample = Now();
            long lastDone = TotalDone();

            while (true)
            {
                bool all = AllSegmentsDone();

                if (all) break;

                if (_cancel)
                {
                    WaitWorkers();
                    if (AllSegmentsDone())
                    {
                        Report(DlState.Merging, "正在合并分块…");
                        Merge();
                        Report(DlState.Finished, "下载完成");
                        Log("下载完成：" + SavePath);
                        return;
                    }
                    CleanUp(true);
                    Report(DlState.Cancelled, "已取消");
                    return;
                }

                if (_pause)
                {
                    WaitWorkers();
                    if (_cancel) { CleanUp(true); Report(DlState.Cancelled, "已取消"); return; }
                    UpdateSpeedSampling(ref lastSample, ref lastDone);
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
                                if (Interlocked.Read(ref s.Done) < s.End - s.Start + 1)
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

                Thread.Sleep(100);
            }

            WaitWorkers();

            if (!AllSegmentsDone())
            {
                if (_cancel) { CleanUp(true); Report(DlState.Cancelled, "已取消"); return; }
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
                    if (Interlocked.Read(ref s.Done) < s.End - s.Start + 1) return false;
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
                _doneSnapshot = done;
                lastTime = now;
                lastDone = done;
            }
        }

        private static long Now()
        {
            return Environment.TickCount;
        }

        private bool TrySplitOne()
        {
            if (_active >= MaxThreads) return false;

            Segment target = null;
            lock (_segLock)
            {
                foreach (var s in _segs)
                {
                    if (Interlocked.Read(ref s.Done) >= s.End - s.Start + 1) continue;
                    if (s.Remaining < PieceLimit) continue;
                    if (target == null || s.Remaining > target.Remaining) target = s;
                }
                if (target == null) return false;

                long remaining = target.Remaining;
                long take = Math.Max(PieceLimit, (long)Math.Round(remaining * 0.4));
                long newStart = target.End + 1 - take;
                if (newStart - target.Start < PieceLimit) return false; // 切完太小，不切

                long oldEnd = Interlocked.Read(ref target.End);
                long newEnd = newStart - 1;

                var seg = new Segment
                {
                    Start = newStart,
                    End = oldEnd,
                    Index = _segs.Count + 1,
                    TempPath = TempPathFor(_segs.Count + 1)
                };
                File.WriteAllBytes(seg.TempPath, new byte[0]);
                _segs.Add(seg);
                Interlocked.Exchange(ref target.End, newEnd);
                Log(string.Format("切分新线程 #{0}：{1} ~ {2}（原线程止于 {3}）",
                    seg.Index, seg.Start, seg.End, newEnd));
                SpawnWorker(seg);
                return true;
            }
        }

        private void SpawnWorker(Segment seg)
        {
            var t = new Thread(() => WorkerMain(seg));
            t.IsBackground = true;
            t.Name = "下载线程 #" + seg.Index;
            lock (_segLock) { _workers.Add(t); }
            Interlocked.Increment(ref _active);
            t.Start();
        }

        private void WaitWorkers()
        {
            List<Thread> list;
            lock (_segLock) { list = new List<Thread>(_workers); }
            foreach (var t in list) t.Join(3000);
        }

        /// <summary>
        /// 单个线程：对 [Start+Done, End] 发 Range 请求，写入自己的临时文件；失败自动续传重试。
        /// </summary>
        private void WorkerMain(Segment seg)
        {
            int attempt = 0;
            try
            {
                while (Interlocked.Read(ref seg.Done) < seg.End - seg.Start + 1)
                {
                    if (_pause || _cancel) return;
                    try
                    {
                        DownloadSegmentOnce(seg);
                        attempt = 0;
                    }
                    catch (Exception ex)
                    {
                        if (_pause || _cancel) return;
                        attempt++;
                        if (attempt >= 2 && _mirrorTries < Mirrors.Count && TryNextMirror())
                        {
                            _mirrorTries++;
                            attempt = 0;
                            Log("线程 #" + seg.Index + " 切换镜像源：" + Url);
                            continue;
                        }
                        if (attempt > 3) throw;
                        Log(string.Format("线程 #{0} 第 {1} 次失败：{2}，1 秒后续传", seg.Index, attempt, ex.Message));
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
                Interlocked.Decrement(ref _active);
            }
        }

        private bool TryNextMirror()
        {
            if (Mirrors == null || Mirrors.Count == 0) return false;
            _srcIdx = (_srcIdx + 1) % Mirrors.Count;
            Url = Mirrors[_srcIdx];
            return true;
        }

        private void DownloadSegmentOnce(Segment seg)
        {
            long from = seg.Start + Interlocked.Read(ref seg.Done);
            long to = Interlocked.Read(ref seg.End);
            if (from > to) return;

            FetchResult resp;
            try
            {
                resp = _http.Fetch(Url, from, to);
            }
            catch (Exception ex)
            {
                FallbackTransport(ex.Message);
                throw;
            }

            using (resp)
            {
                if (resp.Status != 206)
                {
                    if (resp.Status == 200)
                        throw new Exception("服务器忽略了 Range 请求（返回 200），该地址无法分块下载");
                    throw new Exception("HTTP " + resp.Status);
                }

                long expect = to - from + 1;
                long gotLen = resp.ContentLength;
                if (gotLen > 0 && gotLen != expect && from == seg.Start && expect == _totalSize)
                    throw new Exception(string.Format("文件大小与探测不一致：期望 {0}，实际 {1}", expect, gotLen));

                var stream = resp.Body;
                var fs = new FileStream(seg.TempPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                try
                {
                    byte[] buf = new byte[BufferSize];
                    long lastRecv = Now();
                    while (Interlocked.Read(ref seg.Done) < seg.End - seg.Start + 1)
                    {
                        if (_pause || _cancel) return;

                        long remaining = seg.End - seg.Start + 1 - Interlocked.Read(ref seg.Done);
                        int want = (int)Math.Min(BufferSize, remaining);
                        if (want <= 0) break;

                        var readTask = stream.ReadAsync(buf, 0, want);
                        if (!readTask.Wait(30000))
                            throw new Exception("读取超时（30 秒无数据）");
                        int n = readTask.Result;
                        if (n <= 0) break;

                        long room = seg.End - seg.Start + 1 - Interlocked.Read(ref seg.Done);
                        if (room <= 0) break;
                        if (n > room) n = (int)room;

                        fs.Write(buf, 0, n);
                        fs.Flush();
                        Interlocked.Add(ref seg.Done, n);
                        SpeedLimiter.Global.Take(n);

                        long gap = Now() - lastRecv;
                        if (gap > 5000 && n < gap) throw new Exception("速度过慢，主动断开重连");
                        lastRecv = Now();
                    }
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
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath) ?? ".");
            string part = SavePath + ".part";
            long done = 0;
            if (File.Exists(part)) done = new FileInfo(part).Length;
            if (done > 0 && !_rangeSupported) done = 0;
            Interlocked.Exchange(ref _singleDone, done);

            while (done < _totalSize || _unknownSize)
            {
                if (_cancel) { CleanUp(true); Report(DlState.Cancelled, "已取消"); return; }

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

                    var stream = resp.Body;
                    var fs = new FileStream(part, done > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
                    try
                    {
                        byte[] buf = new byte[BufferSize];
                        long lastSample = Now(), lastDone = done;
                        while (true)
                        {
                            if (_pause || _cancel) return;
                            var rt = stream.ReadAsync(buf, 0, BufferSize);
                            if (!rt.Wait(30000)) throw new Exception("读取超时（30 秒无数据）");
                            int n = rt.Result;
                            if (n <= 0) break;
                            fs.Write(buf, 0, n);
                            fs.Flush();
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
                    }
                    finally { fs.Dispose(); stream.Dispose(); }
                }

                if (!_unknownSize && done >= _totalSize) break;
                if (_pause || _cancel) break;
                if (!_rangeSupported) break;
            }

            if (_pause)
            {
                Report(DlState.Paused, "已暂停（下次将继续）");
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
            VerifyFinished();
            Report(DlState.Finished, "下载完成");
            Log("下载完成：" + SavePath);
        }

        // ================= 合并 =================

        private void Merge()
        {
            var ordered = new List<Segment>();
            lock (_segLock) { ordered.AddRange(_segs); }
            ordered.Sort((a, b) => a.Start.CompareTo(b.Start));

            string tmpOut = SavePath + ".merging";
            using (var outFs = new FileStream(tmpOut, FileMode.Create, FileAccess.Write))
            {
                foreach (var seg in ordered)
                {
                    long need = seg.End - seg.Start + 1;
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
            VerifyFinished();

            foreach (var seg in ordered)
            {
                try { if (File.Exists(seg.TempPath)) File.Delete(seg.TempPath); } catch { }
            }
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
                    var sb = new System.Text.StringBuilder(hash.Length * 2);
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
            lock (_segLock)
            {
                foreach (var s in _segs)
                {
                    try { if (File.Exists(s.TempPath)) File.Delete(s.TempPath); } catch { }
                }
                _segs.Clear();
            }
        }
    }
}
