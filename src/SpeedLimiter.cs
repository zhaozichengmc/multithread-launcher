using System;
using System.Diagnostics;
using System.Threading;

namespace PclDl
{
    /// <summary>全局限速：所有任务的线程共享一个令牌节奏，0 = 不限速</summary>
    public class SpeedLimiter
    {
        public static readonly SpeedLimiter Global = new SpeedLimiter();

        private readonly object _lock = new object();
        private long _limit;
        private long _next;   // 下一个可用时刻（单调毫秒）
        private volatile bool _cancelWait;

        private static readonly long ClockBase = Stopwatch.GetTimestamp();
        private static long Now()
        {
            return (long)((Stopwatch.GetTimestamp() - ClockBase) * (1000.0 / Stopwatch.Frequency));
        }

        public long Limit { get { lock (_lock) return _limit; } }

        public void SetLimit(long bytesPerSecond)
        {
            lock (_lock)
            {
                _limit = Math.Max(0, bytesPerSecond);
                _next = 0;
            }
            _cancelWait = true;   // 让正在 sleep 的线程立刻出来，新速率马上生效
        }

        public void CancelWait()
        {
            _cancelWait = true;
        }

        /// <summary>
        /// 按字节数排队，必要时阻塞当前线程，保证总速率不超过上限。
        /// 以前 Sleep 封顶 3 秒，设 1~5 KB/s 时实际能跑几十 KB/s；现在按剩余时间分片 sleep，精度对了。
        /// </summary>
        public void Take(int bytes)
        {
            if (bytes <= 0) return;

            double wait;
            lock (_lock)
            {
                if (_limit <= 0) return;
                long now = Now();
                if (_next < now) _next = now;
                _next += (long)Math.Ceiling(bytes * 1000.0 / _limit);
                wait = _next - now;
            }
            if (wait <= 0) return;

            long end = Now() + (long)wait;
            while (true)
            {
                if (_cancelWait)
                {
                    _cancelWait = false;
                    lock (_lock) { _next = 0; }   // 预算清零，避免暂停/改速率后一次性爆发
                    return;
                }
                long left = end - Now();
                if (left <= 0) return;
                try { Thread.Sleep((int)Math.Min(left, 100)); } catch { return; }
            }
        }
    }
}
