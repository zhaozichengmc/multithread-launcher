using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace PclDlGui
{
    public static class JavaFinder
    {
        public static int Version(string exe)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, "-version")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);
                    string all = (err + "\n" + o);
                    var m = Regex.Match(all, "version \"([^\"]+)\"");
                    if (!m.Success) return 0;
                    string v = m.Groups[1].Value;
                    if (v.StartsWith("1.")) return int.Parse(v.Split('.')[1]);
                    return int.Parse(v.Split('.')[0]);
                }
            }
            catch { return 0; }
        }

        public static List<string> Candidates()
        {
            var list = new List<string>();
            Action<string> addDir = d =>
            {
                try
                {
                    if (!Directory.Exists(d)) return;
                    foreach (var sub in Directory.GetDirectories(d))
                    {
                        string exe = Path.Combine(sub, "bin", "java.exe");
                        if (File.Exists(exe)) list.Add(exe);
                        string exe2 = Path.Combine(sub, "bin", "javaw.exe");
                        if (File.Exists(exe2)) list.Add(exe2);
                    }
                    string direct = Path.Combine(d, "bin", "java.exe");
                    if (File.Exists(direct)) list.Add(direct);
                }
                catch { }
            };

            foreach (var root in new[]
            {
                @"SOFTWARE\JavaSoft\Java Runtime Environment",
                @"SOFTWARE\JavaSoft\Java Development Kit",
                @"SOFTWARE\WOW6432Node\JavaSoft\Java Runtime Environment",
                @"SOFTWARE\WOW6432Node\JavaSoft\Java Development Kit"
            })
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (key == null) continue;
                        foreach (var ver in key.GetSubKeyNames())
                            using (var sub = key.OpenSubKey(ver))
                            {
                                string home = sub?.GetValue("JavaHome") as string;
                                if (!string.IsNullOrEmpty(home)) addDir(home);
                            }
                    }
                }
                catch { }
            }

            string pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            foreach (var d in new[]
            {
                Path.Combine(pf, "Java"), Path.Combine(pf, "Eclipse Adoptium"), Path.Combine(pf, "AdoptOpenJDK"),
                Path.Combine(pf, "Zulu"), Path.Combine(pf, "Microsoft"), Path.Combine(pf, "BellSoft"),
                Path.Combine(pf, "Amazon Corretto"), Path.Combine(pf86, "Java"),
                Path.Combine(local, "Programs", "Eclipse Adoptium"), Path.Combine(local, "Programs", "Java"),
                Path.Combine(local, "Programs", "Microsoft")
            }) addDir(d);

            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
            {
                string exe = Path.Combine(javaHome, "bin", "java.exe");
                if (File.Exists(exe)) list.Add(exe);
            }

            try
            {
                var psi = new ProcessStartInfo("where", "java")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (var line in o.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.EndsWith("java.exe") && File.Exists(t)) list.Add(t);
                    }
                }
            }
            catch { }

            var result = new List<string>();
            foreach (var c in list)
            {
                string use = c;
                if (c.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string w = c.Substring(0, c.Length - 8) + "javaw.exe";
                    if (File.Exists(w)) use = w;
                }
                if (!result.Contains(use)) result.Add(use);
            }
            return result;
        }

        /// <summary>自动挑一个满足最低大版本的 Java；找不到返回 null</summary>
        public static string Find(int needMajor)
        {
            string best = null; int bestVer = -1;
            foreach (var c in Candidates())
            {
                int v = Version(c);
                if (v <= 0) continue;
                if (v >= needMajor)
                {
                    if (best == null || v < bestVer) { best = c; bestVer = v; }
                }
                else if (best == null && v > bestVer) { best = c; bestVer = v; }
            }
            return best;
        }
    }

    public static class Launcher
    {
        private static bool EvalRules(JToken rules, out bool matched)
        {
            matched = false;
            if (rules == null || rules.Type != JTokenType.Array) return true;
            bool allow = false;
            foreach (var r in (JArray)rules)
            {
                bool ok = true;
                var os = r["os"];
                if (os != null)
                {
                    string name = (string)os["name"];
                    if (!string.IsNullOrEmpty(name) && name != "windows") ok = false;
                    string arch = (string)os["arch"];
                    if (!string.IsNullOrEmpty(arch) && (arch == "x86" || arch == "arm64")) ok = false;
                    var vr = os["versionRange"];
                    if (vr != null && ok)
                    {
                        string cur = Environment.OSVersion.Version.ToString();
                        string min = (string)vr["min"];
                        string max = (string)vr["max"];
                        if (!string.IsNullOrEmpty(min) && CompareVer(cur, min) < 0) ok = false;
                        if (!string.IsNullOrEmpty(max) && CompareVer(cur, max) > 0) ok = false;
                    }
                }
                var feat = r["features"];
                if (feat != null) ok = false; // 我们不启用任何可选特性
                if (ok) allow = (string)r["action"] == "allow";
            }
            matched = true;
            return allow;
        }

        private static int CompareVer(string a, string b)
        {
            try
            {
                var va = a.Split('.'); var vb = b.Split('.');
                for (int i = 0; i < Math.Max(va.Length, vb.Length); i++)
                {
                    int x = i < va.Length ? int.Parse(va[i]) : 0;
                    int y = i < vb.Length ? int.Parse(vb[i]) : 0;
                    if (x != y) return x > y ? 1 : -1;
                }
                return 0;
            }
            catch { return 0; }
        }

        private static void Collect(JToken arr, List<string> outValues)
        {
            if (arr == null || arr.Type != JTokenType.Array) return;
            foreach (var item in (JArray)arr)
            {
                if (item.Type == JTokenType.String) { outValues.Add((string)item); continue; }
                if (item.Type != JTokenType.Object) continue;
                bool ok = EvalRules(item["rules"], out _);
                if (!ok) continue;
                var val = item["value"];
                if (val == null) continue;
                if (val.Type == JTokenType.Array)
                    foreach (var v in (JArray)val) outValues.Add((string)v);
                else outValues.Add((string)val);
            }
        }

        public static string Fill(string s, Dictionary<string, string> map)
        {
            foreach (var kv in map) s = s.Replace("${" + kv.Key + "}", kv.Value ?? "");
            return s;
        }

        private static bool Writable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".wtest");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>下载并缓存 authlib-injector，返回 jar 路径</summary>
        public static string EnsureAuthlib(Action<string> log)
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string jar = Path.Combine(dir, "authlib-injector.jar");
            if (!Writable(dir))
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCLDownloader");
                Directory.CreateDirectory(dir);
                jar = Path.Combine(dir, "authlib-injector.jar");
            }
            if (File.Exists(jar) && new FileInfo(jar).Length > 100000) return jar;
            string[] latestUrls = {
                "https://authlib-injector.yushi.moe/artifact/latest.json",
                "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json"
            };
            Exception last = null;
            foreach (var lu in latestUrls)
            {
                try
                {
                    var j = JObject.Parse(Http.Get(lu));
                    string url = (string)j["download_url"];
                    if (string.IsNullOrEmpty(url)) continue;
                    string tmp = jar + ".tmp";
                    // WebClient 无超时，authlib 站挂住会把"启动游戏"卡死在这里
                    McInstaller.FetchToFile(url, tmp);
                    if (!File.Exists(tmp) || new FileInfo(tmp).Length < 100000)
                        throw new Exception("下载到的 authlib-injector 不是有效 jar");
                    if (File.Exists(jar)) File.Delete(jar);
                    File.Move(tmp, jar);
                    log("authlib-injector 已就绪：" + Path.GetFileName(jar));
                    return jar;
                }
                catch (Exception ex) { last = ex; }
            }
            throw new Exception("authlib-injector 下载失败：" + (last?.Message ?? "未知错误"));
        }

        /// <summary>该实例的客户端 jar 在哪：自己目录有就用，没有就顺 inheritsFrom 往上找父版本的 jar</summary>
        public static bool ResolveJar(string dir, string id, out string jarPath)
        {
            jarPath = null;
            string cur = id;
            for (int depth = 0; depth < 4 && !string.IsNullOrEmpty(cur); depth++)
            {
                string vd = Path.Combine(dir, "versions", cur);
                string jar = Path.Combine(vd, cur + ".jar");
                if (File.Exists(jar)) { jarPath = jar; return true; }
                string js = Path.Combine(vd, cur + ".json");
                if (!File.Exists(js)) return false;
                string parent = null;
                try { parent = (string)JObject.Parse(File.ReadAllText(js))["inheritsFrom"]; }
                catch { return false; }
                if (string.IsNullOrEmpty(parent)) return false;
                cur = parent;
            }
            return false;
        }

        /// <summary>本地实例是否可启动：有 json，且能找到客户端 jar（含继承父版本）</summary>
        public static bool IsInstalled(string dir, string id)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(id)) return false;
            string js = Path.Combine(dir, "versions", id, id + ".json");
            if (!File.Exists(js)) return false;
            string jar;
            return ResolveJar(dir, id, out jar);
        }

        /// <summary>该实例需要的最低 Java 主版本（会顺 inheritsFrom 找父版本的 javaVersion）</summary>
        public static int NeedJava(string dir, string id)
        {
            try { return (int?)(LoadMerged(dir, id)["javaVersion"]?["majorVersion"]) ?? 8; }
            catch { return 8; }
        }

        /// <summary>把继承链（Forge / Fabric / 整合包）上的 json 合并成一份可启动的版本描述</summary>
        private static JObject LoadMerged(string dir, string id)
        {
            var stack = new List<JObject>();
            string cur = id;
            for (int depth = 0; depth < 4 && !string.IsNullOrEmpty(cur); depth++)
            {
                string js = Path.Combine(dir, "versions", cur, cur + ".json");
                if (!File.Exists(js)) break;
                JObject j;
                try { j = JObject.Parse(File.ReadAllText(js)); }
                catch (Exception ex) { throw new Exception("版本 json 解析失败（" + cur + "）：" + ex.Message); }
                stack.Add(j);
                cur = (string)j["inheritsFrom"];
            }
            if (stack.Count == 0) throw new Exception("找不到版本 json：" + id);
            JObject merged = (JObject)stack[stack.Count - 1].DeepClone();
            for (int i = stack.Count - 2; i >= 0; i--)
                merged = MergeVersion(merged, stack[i]);
            return merged;
        }

        /// <summary>parent 作底、child 覆盖。libraries 按 name 合并，arguments 按 jvm/game 追加</summary>
        private static JObject MergeVersion(JObject parent, JObject child)
        {
            var result = (JObject)parent.DeepClone();
            foreach (var p in child.Properties())
            {
                if (p.Name == "libraries" && p.Value.Type == JTokenType.Array &&
                    result["libraries"] != null && result["libraries"].Type == JTokenType.Array)
                {
                    var mergedLibs = new JArray();
                    var keep = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
                    foreach (var l in (JArray)result["libraries"])
                    {
                        string key = (string)l["name"];
                        if (string.IsNullOrEmpty(key))
                        {
                            mergedLibs.Add(l.DeepClone());
                            continue;
                        }
                        keep[key] = l;
                        mergedLibs.Add(l.DeepClone());
                    }
                    foreach (var l in (JArray)p.Value)
                    {
                        string key = (string)l["name"];
                        if (!string.IsNullOrEmpty(key) && keep.ContainsKey(key))
                        {
                            for (int i = mergedLibs.Count - 1; i >= 0; i--)
                                if ((string)mergedLibs[i]["name"] == key) { mergedLibs.RemoveAt(i); break; }
                        }
                        mergedLibs.Add(l.DeepClone());
                    }
                    result["libraries"] = mergedLibs;
                }
                else if (p.Name == "arguments" && p.Value.Type == JTokenType.Object &&
                         result["arguments"] != null && result["arguments"].Type == JTokenType.Object)
                {
                    var args = (JObject)result["arguments"];
                    foreach (var ap in ((JObject)p.Value).Properties())
                    {
                        if (ap.Value.Type == JTokenType.Array && args[ap.Name] != null && args[ap.Name].Type == JTokenType.Array)
                        {
                            var comb = new JArray();
                            foreach (var x in (JArray)args[ap.Name]) comb.Add(x.DeepClone());
                            foreach (var x in (JArray)ap.Value) comb.Add(x.DeepClone());
                            args[ap.Name] = comb;
                        }
                        else args[ap.Name] = ap.Value.DeepClone();
                    }
                }
                else
                {
                    result[p.Name] = p.Value.DeepClone();
                }
            }
            return result;
        }

        /// <summary>"group:artifact:version[:classifier]" → libraries 下的相对路径</summary>
        private static string MavenToPath(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var parts = name.Split(':');
            if (parts.Length < 3) return null;
            string group = parts[0], artifact = parts[1], version = parts[2];
            string classifier = parts.Length > 3 ? parts[3] : null;
            string file = artifact + "-" + version + (string.IsNullOrEmpty(classifier) ? "" : "-" + classifier) + ".jar";
            return group.Replace('.', '/') + "/" + artifact + "/" + version + "/" + file;
        }

        /// <summary>natives 目录也顺着继承链找：整合包/Forge 子版本往往没有自己的 natives，只有父版本有</summary>
        private static string ResolveNatives(string dir, string id, string fallback)
        {
            string cur = id;
            for (int depth = 0; depth < 4 && !string.IsNullOrEmpty(cur); depth++)
            {
                string nd = Path.Combine(dir, "versions", cur, "natives");
                try
                {
                    if (Directory.Exists(nd) && Directory.GetFiles(nd, "*.dll").Length > 0) return nd;
                }
                catch { }
                string js = Path.Combine(dir, "versions", cur, cur + ".json");
                if (!File.Exists(js)) break;
                try { cur = (string)JObject.Parse(File.ReadAllText(js))["inheritsFrom"]; }
                catch { break; }
            }
            return fallback;
        }

        public static Process Launch(string id, string dir, Account acc, string javaExe, int memMb, Action<string> log)
        {
            var psi = Build(id, dir, acc, javaExe, memMb, log);
            log("启动：" + id + " · " + acc.Name + " · " + memMb + "MB");
            return Process.Start(psi);
        }

        public static ProcessStartInfo Build(string id, string dir, Account acc, string javaExe, int memMb, Action<string> log)
        {
            string versionDir = Path.Combine(dir, "versions", id);
            string jsonPath = Path.Combine(versionDir, id + ".json");
            if (!File.Exists(jsonPath))
                throw new Exception("找不到版本 json：" + jsonPath);
            string jarPath;
            if (!ResolveJar(dir, id, out jarPath))
                throw new Exception("该实例缺少客户端 jar（也找不到继承的父版本 jar），请重新安装");

            JObject v = LoadMerged(dir, id);
            int needJava = (int?)(v["javaVersion"]?["majorVersion"]) ?? 8;
            int haveJava = JavaFinder.Version(javaExe);
            if (haveJava > 0 && haveJava < needJava)
                throw new Exception("该版本需要 Java " + needJava + "，当前选中的是 Java " + haveJava + "。\n请安装或用“浏览”指定 java.exe。");
            if (haveJava == 0) throw new Exception("无法运行该 Java：路径不对或文件损坏");

            string natives = ResolveNatives(dir, id, Path.Combine(versionDir, "natives"));
            if (!Directory.Exists(natives)) Directory.CreateDirectory(natives);
            string libDir = Path.Combine(dir, "libraries");
            string assets = Path.Combine(dir, "assets");

            // ---- classpath ----
            var cp = new List<string>();
            var libs = v["libraries"] as JArray;
            if (libs != null)
            {
                foreach (var lib in libs)
                {
                    bool ok = EvalRules(lib["rules"], out _);
                    if (!ok) continue;
                    // Forge / Fabric 的 json 里很多库只写 maven 坐标（name），没有 downloads.artifact.path，
                    // 以前直接 continue 会让 classpath 缺 mod 加载器 → 点启动就崩。
                    string rel = null;
                    var art = lib["downloads"]?["artifact"];
                    if (art != null) rel = (string)art["path"];
                    if (string.IsNullOrEmpty(rel)) rel = MavenToPath((string)lib["name"]);
                    if (string.IsNullOrEmpty(rel)) continue;
                    if (rel.IndexOf("natives-windows", StringComparison.OrdinalIgnoreCase) >= 0) continue; // 原生库不进 classpath
                    string p = Path.Combine(libDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(p)) cp.Add(p);
                    else log("警告：缺少支持库 " + rel);
                }
            }
            cp.Add(jarPath);
            string classpath = string.Join(";", cp.ToArray());

            string assetsIndex = (string)v["assetIndex"]?["id"] ?? (string)v["assets"] ?? "legacy";

            var map = new Dictionary<string, string>
            {
                { "auth_player_name", acc.Name },
                { "version_name", id },
                { "game_directory", dir },
                { "assets_root", assets },
                { "assets_index_name", assetsIndex },
                { "game_assets", assets },
                { "auth_uuid", (acc.Uuid ?? "").Replace("-", "") },
                { "auth_access_token", acc.AccessToken ?? "0" },
                { "auth_session", acc.AccessToken ?? "0" },
                { "user_type", acc.UserType ?? "legacy" },
                { "user_properties", "{}" },
                { "clientid", "" },
                { "auth_xuid", acc.Xuid ?? "" },
                { "version_type", (string)v["type"] ?? "release" },
                { "launcher_name", "PCLDownloader" },
                { "launcher_version", "2.4" },
                { "resolution_width", "854" },
                { "resolution_height", "480" },
                { "natives_directory", natives },
                { "classpath", classpath },
                { "classpath_separator", ";" },
                { "library_directory", libDir },
                { "quickPlayPath", "" },
                { "quickPlaySingleplayer", "" },
                { "quickPlayMultiplayer", "" },
                { "quickPlayRealms", "" },
                { "auth_player_uuid", (acc.Uuid ?? "").Replace("-", "") }
            };

            var args = new List<string>();
            args.Add("-Xms512M");
            args.Add("-Xmx" + Math.Max(512, memMb) + "M");
            args.Add("-Dfile.encoding=UTF-8");
            args.Add("-Dstdout.encoding=UTF-8");
            args.Add("-Dstderr.encoding=UTF-8");

            if (acc.Type == "third")
            {
                try
                {
                    string jar = EnsureAuthlib(log);
                    args.Add("-javaagent:" + jar + "=" + acc.Server);
                    args.Add("-Dauthlibinjector.side=client");
                    try
                    {
                        string meta = Http.Get(acc.Server, 1);
                        args.Add("-Dauthlibinjector.yggdrasil.prefetched=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(meta)));
                    }
                    catch { }
                }
                catch (Exception ex) { throw new Exception(ex.Message); }
            }

            var jvmArr = v["arguments"]?["jvm"];
            if (jvmArr != null)
            {
                var jvm = new List<string>();
                Collect(jvmArr, jvm);
                foreach (var a in jvm) args.Add(Fill(a, map));
            }
            else
            {
                args.Add("-Djava.library.path=" + natives);
                args.Add("-cp");
                args.Add(classpath);
            }

            string mainClass = (string)v["mainClass"];
            if (string.IsNullOrEmpty(mainClass))
                throw new Exception("版本 json 里没有 mainClass，安装文件可能不完整");
            args.Add(mainClass);

            if (v["minecraftArguments"] != null)
            {
                // 不能按空格硬切：昵称/路径带空格、或参数是空串 "" 时会整体错位
                foreach (var a in SplitArgs((string)v["minecraftArguments"]))
                    args.Add(Fill(a, map));
            }
            else
            {
                var game = new List<string>();
                Collect(v["arguments"]?["game"], game);
                foreach (var a in game) args.Add(Fill(a, map));
            }

            string cmdLine = Quote(javaExe) + " " + string.Join(" ", QuoteAll(args).ToArray());
            try
            {
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCLDownloader");
                Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, "last_launch.txt"), cmdLine, new UTF8Encoding(false));
            }
            catch { }

            var psi = new ProcessStartInfo(javaExe, string.Join(" ", QuoteAll(args).ToArray()))
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            return psi;
        }

        /// <summary>按 Windows 规则切分参数串：支持双引号、引号内空格、\" 转义和空参数</summary>
        private static List<string> SplitArgs(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;

            var sb = new StringBuilder();
            bool inQuote = false, hasToken = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length && s[i + 1] == '"')
                {
                    sb.Append('"'); i++; hasToken = true; continue;
                }
                if (c == '"')
                {
                    inQuote = !inQuote; hasToken = true; continue;
                }
                if (!inQuote && (c == ' ' || c == '\t'))
                {
                    if (hasToken) { list.Add(sb.ToString()); sb.Length = 0; hasToken = false; }
                    continue;
                }
                sb.Append(c);
                hasToken = true;
            }
            if (hasToken) list.Add(sb.ToString());
            return list;
        }

        private static List<string> QuoteAll(List<string> args)
        {
            var l = new List<string>();
            foreach (var a in args) l.Add(Quote(a));
            return l;
        }

        /// <summary>
        /// 按 CreateProcess / CommandLineToArgvW 的规则加引号：以前用 \" 转义是错的，
        /// 参数里带引号或结尾反斜杠时 JVM 会收到错位的字符串。
        /// </summary>
        private static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            bool need = false;
            foreach (char c in s)
                if (c == ' ' || c == '\t' || c == '"') { need = true; break; }
            if (!need) return s;

            var sb = new StringBuilder(s.Length + 4);
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
                    sb.Append(c);
                }
            }
            if (backslashes > 0) sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
