using System;
using System.IO;
using System.Collections.Generic;
using PclDl;

namespace PclDlGui
{
    /// <summary>队列里的一个下载任务</summary>
    public class TaskItem
    {
        public string Url;
        public string Path;
        public int Threads = 16;
        public string Sha256;                 // 期望值，可空
        public string Referer;                // 内置浏览器带过来的来源页
        public string Cookie;                 // 内置浏览器带过来的 Cookie（不持久化）
        public List<string> Mirrors = new List<string>();

        public DlState State = DlState.Idle;
        public long Done;
        public long Total;
        public long Speed;
        public long PeakSpeed;
        public int ActiveThreads;
        public string Message;
        public bool Running;

        public MultiThreadDownloader Dl;
        public System.Windows.Forms.ListViewItem Row;

        public string Name
        {
            get { return System.IO.Path.GetFileName(Path ?? Url); }
        }

        public int Percent
        {
            get { return Total > 0 ? (int)Math.Min(100, Done * 100 / Total) : 0; }
        }

        public string SizeText
        {
            get
            {
                if (Total <= 0) return Done > 0 ? MultiThreadDownloader.FormatSize(Done) + " / ?" : "—";
                return MultiThreadDownloader.FormatSize(Total);
            }
        }

        public void ResetProgress()
        {
            Done = 0; Total = 0; Speed = 0; PeakSpeed = 0; ActiveThreads = 0; Message = null;
        }
    }
}
