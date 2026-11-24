namespace PclDl
{
    /// <summary>代理设置：HTTP 或 SOCKS5</summary>
    public class ProxyConfig
    {
        public string Host;
        public int Port;
        public bool Socks5;
        public string User;
        public string Pass;

        public bool Valid
        {
            get { return !string.IsNullOrEmpty(Host) && Port > 0 && Port < 65536; }
        }

        public override string ToString()
        {
            if (!Valid) return "系统代理";
            string auth = string.IsNullOrEmpty(User) ? "" : (User + (string.IsNullOrEmpty(Pass) ? "" : ":" + Pass) + "@");
            return (Socks5 ? "SOCKS5 " : "HTTP ") + auth + Host + ":" + Port;
        }
    }
}
