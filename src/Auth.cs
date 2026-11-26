using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PclDlGui
{
    public class Account
    {
        public string Type = "offline";   // offline / third / ms
        public string Name = "";
        public string Uuid = "";
        public string AccessToken = "";
        public string RefreshToken = "";
        public string Server = "";        // 第三方皮肤站 api 根
        public string Xuid = "";
        public string UserType = "legacy";
    }

    public static class AccountStore
    {
        private static string PathFile
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCLDownloader");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "account.json");
            }
        }

        public static Account Load()
        {
            try
            {
                if (!File.Exists(PathFile)) return new Account();
                return Newtonsoft.Json.JsonConvert.DeserializeObject<Account>(File.ReadAllText(PathFile)) ?? new Account();
            }
            catch { return new Account(); }
        }

        public static void Save(Account a)
        {
            try { File.WriteAllText(PathFile, Newtonsoft.Json.JsonConvert.SerializeObject(a), new UTF8Encoding(false)); }
            catch { }
        }

        public static void Clear()
        {
            try { if (File.Exists(PathFile)) File.Delete(PathFile); } catch { }
        }
    }

    public static class Http
    {
        static Http()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try { ServicePointManager.Expect100Continue = false; } catch { }
        }

        public static string PostForm(string url, string body, int retries = 2)
        {
            return Request(url, "POST", body, "application/x-www-form-urlencoded", retries);
        }

        public static string PostJson(string url, string body, int retries = 2)
        {
            return Request(url, "POST", body, "application/json", retries);
        }

        public static string Get(string url, int retries = 2)
        {
            return Request(url, "GET", null, null, retries);
        }

        private static string Request(string url, string method, string body, string contentType, int retries)
        {
            Exception last = null;
            for (int i = 0; i < retries + 1; i++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = method;
                    req.UserAgent = "PCLDownloader/2.3";
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 30000;
                    req.AllowAutoRedirect = true;
                    if (body != null)
                    {
                        req.ContentType = contentType;
                        byte[] data = Encoding.UTF8.GetBytes(body);
                        req.ContentLength = data.Length;
                        using (var st = req.GetRequestStream()) st.Write(data, 0, data.Length);
                    }
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return sr.ReadToEnd();
                }
                catch (WebException ex)
                {
                    last = ex;
                    try
                    {
                        var r = ex.Response as HttpWebResponse;
                        if (r != null)
                            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                        {
                            string err = sr.ReadToEnd();
                            if (!string.IsNullOrEmpty(err)) last = new Exception(err);
                        }
                    }
                    catch { }
                    Thread.Sleep(500 * (i + 1));
                }
                catch (Exception ex) { last = ex; Thread.Sleep(300); }
            }
            throw last ?? new Exception("网络请求失败");
        }
    }

    public static class Auth
    {
        // PCL2 使用的 Azure 应用
        public const string MsClientId = "fe72edc2-3a6f-4280-90e8-e2beb64ce7e1";
        private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
        private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

        public static string OfflineUuid(string name)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
                return new Guid(new byte[]
                {
                    (byte)((hash[6] & 0x0f) | 0x30), hash[7], hash[4], hash[5],
                    hash[0], hash[1], hash[2], hash[3],
                    hash[8], hash[9], hash[10], hash[11], hash[12], hash[13], hash[14], hash[15]
                }).ToString("N");
            }
        }

        public static Account Offline(string name)
        {
            return new Account
            {
                Type = "offline",
                Name = name,
                Uuid = OfflineUuid(name),
                AccessToken = "0",
                UserType = "legacy"
            };
        }

        // ---------------- 微软正版 ----------------
        public class MsCode
        {
            public string DeviceCode, UserCode, VerificationUri;
            public int Interval = 5, ExpiresIn = 900;
        }

        public static MsCode MsStart()
        {
            string body = "client_id=" + MsClientId + "&scope=" + Uri.EscapeDataString("XboxLive.signin offline_access");
            var j = JObject.Parse(Http.PostForm(DeviceCodeUrl, body));
            return new MsCode
            {
                DeviceCode = (string)j["device_code"],
                UserCode = (string)j["user_code"],
                VerificationUri = (string)j["verification_uri"] ?? "https://microsoft.com/link",
                Interval = (int?)j["interval"] ?? 5,
                ExpiresIn = (int?)j["expires_in"] ?? 900
            };
        }

        /// <summary>轮询设备码授权，成功返回账号；超时/拒绝抛异常</summary>
        public static Account MsPoll(MsCode code, CancellationToken ct, Action<string> log)
        {
            var deadline = DateTime.Now.AddSeconds(code.ExpiresIn);
            while (DateTime.Now < deadline)
            {
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(2, code.Interval)));
                if (ct.IsCancellationRequested) throw new OperationCanceledException();
                string body = "client_id=" + MsClientId
                            + "&grant_type=" + Uri.EscapeDataString("urn:ietf:params:oauth:grant-type:device_code")
                            + "&device_code=" + Uri.EscapeDataString(code.DeviceCode);
                try
                {
                    var j = JObject.Parse(Http.PostForm(TokenUrl, body, 1));
                    string msToken = (string)j["access_token"];
                    string refresh = (string)j["refresh_token"];
                    if (string.IsNullOrEmpty(msToken)) continue;
                    return MsExchange(msToken, refresh, log);
                }
                catch (Exception ex)
                {
                    string msg = ex.Message ?? "";
                    if (msg.Contains("authorization_pending")) continue;
                    if (msg.Contains("slow_down")) { code.Interval += 2; continue; }
                    if (msg.Contains("expired_token")) throw new Exception("登录代码已过期，请重新获取");
                    if (msg.Contains("authorization_declined")) throw new Exception("已拒绝授权");
                    log("等待授权：" + msg);
                }
            }
            throw new Exception("登录超时，请重新获取代码");
        }

        public static Account MsRefresh(Account old, Action<string> log)
        {
            if (string.IsNullOrEmpty(old.RefreshToken)) throw new Exception("没有可用的刷新令牌，请重新登录");
            string body = "client_id=" + MsClientId
                        + "&grant_type=refresh_token"
                        + "&refresh_token=" + Uri.EscapeDataString(old.RefreshToken)
                        + "&scope=" + Uri.EscapeDataString("XboxLive.signin offline_access");
            var j = JObject.Parse(Http.PostForm(TokenUrl, body));
            return MsExchange((string)j["access_token"], (string)j["refresh_token"], log);
        }

        private static Account MsExchange(string msToken, string refresh, Action<string> log)
        {
            // 1) Xbox Live
            string xblBody = "{\"Properties\":{\"AuthMethod\":\"RPS\",\"SiteName\":\"user.auth.xboxlive.com\",\"RpsTicket\":\"d=" + msToken + "\"},\"RpsTicket\":\"d=" + msToken + "\",\"TokenType\":\"JWT\"}";
            var xbl = JObject.Parse(Http.PostJson("https://user.auth.xboxlive.com/user/authenticate", xblBody));
            string xblToken = (string)xbl["Token"];
            string uhs = (string)xbl["DisplayClaims"]?["xui"]?[0]?["uhs"];

            // 2) XSTS
            string xstsBody = "{\"Properties\":{\"SandboxId\":\"RETAIL\",\"UserTokens\":[\"" + xblToken + "\"]},\"RpsTicket\":\"" + xblToken + "\",\"TokenType\":\"JWT\"}";
            string xstsJson;
            try { xstsJson = Http.PostJson("https://xsts.auth.xboxlive.com/xsts/authorize", xstsBody); }
            catch (Exception ex)
            {
                // XSTS 的报错是带 XErr 的 JSON，直接把原文给用户等于看不懂
                string m = ex.Message ?? "";
                if (m.Contains("2148916233")) throw new Exception("这个微软账号还没有 Xbox 档案，请先在 xbox.com 登录一次再回来");
                if (m.Contains("2148916235")) throw new Exception("该账号所在地区不支持 Xbox Live（需要换区或换账号）");
                if (m.Contains("2148916238")) throw new Exception("未成年账号需要家长在微软家庭组里批准后才能登录");
                if (m.Contains("2148916227")) throw new Exception("该微软账号已被 Xbox Live 封禁");
                throw new Exception("Xbox 身份验证失败：" + m);
            }
            var xsts = JObject.Parse(xstsJson);
            string xstsToken = (string)xsts["Token"];
            string uhs2 = (string)xsts["DisplayClaims"]?["xui"]?[0]?["uhs"] ?? uhs;

            // 3) Minecraft 服务
            string mcBody = "{\"identityToken\":\"XBL3.0 x=" + uhs2 + ";" + xstsToken + "\"}";
            var mc = JObject.Parse(Http.PostJson("https://api.minecraftservices.com/authentication/login_with_xbox", mcBody));
            string mcToken = (string)mc["access_token"];

            // 4) 档案 + 拥有校验
            var profile = JObject.Parse(Http.Get("https://api.minecraftservices.com/minecraft/profile", 1));
            string name = (string)profile["name"];
            string id = (string)profile["id"];
            if (string.IsNullOrEmpty(name))
            {
                string err = (string)profile["errorMessage"];
                throw new Exception("该微软账户没有 Minecraft Java 版" + (string.IsNullOrEmpty(err) ? "" : "：" + err));
            }
            log("正版登录成功：" + name);
            return new Account
            {
                Type = "ms",
                Name = name,
                Uuid = id,
                AccessToken = mcToken,
                RefreshToken = refresh,
                Xuid = (string)profile["id"],
                UserType = "msa"
            };
        }

        // ---------------- 第三方（authlib-injector / 皮肤站） ----------------
        public static string NormalizeServer(string s)
        {
            s = (s ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(s)) throw new Exception("请填写皮肤站地址");
            if (!s.StartsWith("http")) s = "https://" + s;
            if (!s.EndsWith("/api/yggdrasil") && !s.Contains("/api/yggdrasil"))
            {
                if (s.EndsWith("/api")) s += "/yggdrasil";
                else s += "/api/yggdrasil";
            }
            return s;
        }

        public static Account ThirdLogin(string server, string user, string pass, Action<string> log)
        {
            server = NormalizeServer(server);
            string body = "{\"username\":\"" + Escape(user) + "\",\"password\":\"" + Escape(pass) + "\",\"agent\":{\"name\":\"Minecraft\",\"version\":\"1\"},\"requestUser\":true,\"clientToken\":\"pcl-downloader\"}";
            JObject j;
            try { j = JObject.Parse(Http.PostJson(server + "/authserver/authenticate", body)); }
            catch (Exception ex) { throw new Exception("皮肤站登录失败：" + ex.Message); }
            string token = (string)j["accessToken"];
            var prof = j["selectedProfile"];
            if (string.IsNullOrEmpty(token) || prof == null) throw new Exception("皮肤站登录失败：账号或密码不对");
            log("第三方登录成功：" + (string)prof["name"]);
            return new Account
            {
                Type = "third",
                Name = (string)prof["name"],
                Uuid = (string)prof["id"],
                AccessToken = token,
                Server = server,
                UserType = "mojang"
            };
        }

        public static bool ThirdValidate(Account a)
        {
            try
            {
                var j = JObject.Parse(Http.PostJson(a.Server + "/authserver/validate",
                    "{\"accessToken\":\"" + Escape(a.AccessToken) + "\",\"clientToken\":\"pcl-downloader\"}", 1));
                return true;
            }
            catch { return false; }
        }

        public static Account ThirdRefresh(Account a)
        {
            var j = JObject.Parse(Http.PostJson(a.Server + "/authserver/refresh",
                "{\"accessToken\":\"" + Escape(a.AccessToken) + "\",\"requestUser\":true,\"clientToken\":\"pcl-downloader\"}"));
            a.AccessToken = (string)j["accessToken"];
            var prof = j["selectedProfile"];
            if (prof != null) { a.Name = (string)prof["name"]; a.Uuid = (string)prof["id"]; }
            return a;
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        // 其余控制字符也必须转义，否则拼出来的 JSON 直接非法
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
