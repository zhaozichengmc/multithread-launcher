using System;
using System.Threading;

namespace PclDl
{
    /// <summary>全局限速：所有任务的线程共享一个令牌节奏，0 = 不限速</summary>
    public class SpeedLimiter
    {
        public static readonly SpeedLimiter Global = new SpeedLimiter();

        private readonly object _lock = new object();
        private long _limit;
        private long _next;   // 下一个可用时刻（TickCount 毫秒）

        public long Limit { get { lock (_lock) return _limit; } }

        public void SetLimit(long bytesPerSecond)
        {
            lock (_lock)
            {
                _limit = Math.Max(0, bytesPerSecond);
                _next = 0;
            }
        }

        /// <summary>按字节数排队，必要时阻塞当前线程，保证总速率不超过上限</summary>
        public void Take(int bytes)
        {
            if (bytes <= 0) return;

            double wait;
            lock (_lock)
            {
                if (_limit <= 0) return;
                long now = Environment.TickCount;
                if (_next < now) _next = now;
                _next += (long)(bytes * 1000.0 / _limit);
                wait = _next - now;
            }

            if (wait > 0)
            {
                try { Thread.Sleep((int)Math.Min(wait, 3000)); } catch { }
            }
        }
    }
}
