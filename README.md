# 光速下载器

下载内核源自 PCL2 v2.12 的 `ModNet.cs` 分块算法，扩展成独立的 Windows 下载工具。
net48 / WinForms，单 exe，Win10 自带运行库，无需安装 .NET。

## 功能一览

**内置浏览器**
- 主界面「浏览网页」打开 WebView2（Edge 内核）窗口，网页里点下载自动拦截进队列
- 拦截时带上当前页 Referer 与该 URL 的登录 Cookie，需要登录的站点也能下
- 单文件 exe 缺 WebView2 组件时会自动降级为「系统浏览器」按钮，不影响其他功能
- 完整内置浏览器需解压整个文件夹（exe + 3 个 dll），见发布包里的使用说明

**下载内核**
- 动态分块：首线程探测 Content-Length，其余线程 `Range: bytes=a-b`，从"剩余最多"的分块尾部切走 40%，连接慢了自动多开
- 1~256 线程；小于 1MB 或服务器不支持 Range 自动降级单线程
- 断线续传：每线程独立临时文件，失败自动重连续传，合并时按区间核对大小
- 显示适配：默认跟随系统 DPI（125%/150% 不再错位、字不再偏小），设置里也可锁定 100%~150%；窗口超出屏幕工作区（如 1366×768）会自动收缩到能完整显示，切换显示器时重新计算
- 任务状态修复：单个分块重试用尽不再把整个任务判为“已取消”，会先自动重新拉起失败分块（最多 2 轮），仍失败才报“失败”并保留已下数据
- 保存可靠性：队列 / 设置改 UTF-8 + 临时文件原子替换；保存目录不存在会自动创建，写不进去会在日志里给出具体原因
- 多任务队列：并发任务数可调（1~8），每任务独立的线程数、进度、暂停/继续/取消/重试/移除
- 全局限速（KB/s，0 不限）：所有任务所有线程共享一个令牌节奏
- 失败自动换镜像：可配置多个备用 URL，主源 404/超时/失败自动切换
- 文件名冲突自动改名（xxx (2).iso）；完成后可选 SHA-256 校验（单任务设置，或全局总是计算）

**网络**
- DNS 优选：并行测 7 家公共 DNS + 系统 DNS，TCP 握手打分选最快，TLS 仍按域名校验证书，结果缓存 5 分钟
- 网络调优：下载前把窗口自动调优 / RSS / 时间戳调到吞吐优先，结束（完成/失败/取消）按快照原样还原；快照落盘，进程被强杀时下次启动补还原
- 每连接内核缓冲 2MB/1MB、关 Nagle、HTTP 连接上限 1024
- 代理：HTTP 代理与 SOCKS5（含用户名密码），支持 CONNECT 隧道
- 自定义请求头：Referer / Cookie / User-Agent
- 自动识别系统代理，避免绕过代理导致连不上

**使用**
- 批量：多行粘贴、拖入文本、剪贴板自动捕获（可开关）
- 队列持久化：`%APPDATA%\PCLDownloader\tasks.tsv`，重启恢复任务列表（未完成的重新开始）
- 设置持久化：`%APPDATA%\PCLDownloader\settings.tsv`
- 后台：最小化或关闭窗口收进托盘继续下载；托盘图标是进度环，显示总进度与总速度；右键可暂停 / 取消 / 退出
- 完成后动作：不操作 / 打开文件夹 / 睡眠 / 关机（60 秒，可 `shutdown /a` 取消）
- 命令行：`多线程下载器.exe https://xxx/a.iso` 直接入队（可配合浏览器"用程序打开"）
- 单实例：重复启动静默退出，避免多开抢带宽
- 日志一键导出为 txt

## 算法来源与差异

| 项目 | PCL2（ModNet.cs） | 本工具 |
|---|---|---|
| 分块下限 | 256KB | 同 |
| 动态切分 | 从剩余最多分块切 40% | 同，每轮调度最多切 12 段 |
| 线程上限 | 设置值 + 1 | 1~256 |
| 低速断开 | `gap>5000 && n<gap` | 同 |
| 传输层 | .NET HttpClient | 自建 Socket（可指定 DNS IP）+ HttpClient 双通道 |

## 限制

- 只支持 http/https 直链；ED2K、磁力、需要登录鉴权的页面下载不支持
- 没有跨进程断点续传（重启后未完成任务从头开始，已完成的文件保留）
- 系统级网络调优需要管理员权限；非管理员仅做每连接缓冲优化
- 关机动作会直接执行 `shutdown /s /t 60`，请自行确认队列内容


测试机：2 核 Linux 容器（AMD EPYC），回环网络，50 条并发长连接、每条约 10 请求：

| 接口 | 吞吐 |
|---|---|
| GET /api/health | 约 3.8k 请求/秒 |
| GET /api/me（带令牌） | 约 3.7k 请求/秒 |
| POST /api/login（含 PBKDF2） | 约 300 次/秒 |
| POST /api/register | 约 287 次/秒 |

登录/注册受密码哈希限制（10k 次 PBKDF2-SHA256，故意做慢以抗暴力破解），加核基本线性提升。

为达到上述数字做的优化：
- 密码哈希移到锁外计算，多核可并行；注册用原子 TryAdd 防重复
- 用户表、令牌表、云备份全部内存缓存，每 2 秒批量原子落盘（进程被杀最多丢 2 秒内的新令牌）
- 请求日志进无锁队列，后台每 300ms 批量打印，洪峰丢日志也不阻塞请求
- 线程池最小线程预热到 64，HttpListener 忽略客户端断开写异常
- 令牌每 60 秒清理过期项

容量建议：
- 家庭 / 班级 / 小队（10~50 人）：任意双核机器足够
- 百人以上或公网：4 核 + SSD，前面套 Nginx/Caddy 做 HTTPS 反代
- 单机测试到 50 并发无错误；再高的并发瓶颈先出现在密码哈希，而不是网络层

## 下载 Minecraft 原版（v2.2 新增，2026-11）
主界面「下载 MC」→ 选正式版/快照 → 选 .minecraft 目录 → 一键安装原版：

- 版本清单：`bmclapi2.bangbang93.com/mc/game/version_manifest.json`，失败自动切官方 `piston-meta.mojang.com`
- 版本 JSON / 客户端 jar：BMCL `/version/{id}/json`、`/version/{id}/client`，失败回退官方 URL
- libraries：`libraries.minecraft.net` 改写为 BMCL `/maven`；按 Windows x64 规则过滤（跳过 linux/macos/x86/arm64）
- natives：兼容旧版 `downloads.classifiers` 与新版 `natives-windows` 独立 artifact，自动解压 `.dll` 到 `versions/<id>/natives`
- assets：`resources.download.minecraft.net` 改写为 BMCL `/assets`，对象按 hash 落盘、大小一致即跳过，8 并发
- log4j 日志配置一并下载
- 镜像 503/超时会标记降级 10 分钟并优先走官方源，恢复后自动回到 BMCL
- 实测：1.7.10（686 资源 / 14 dll）、1.20.1（3600 资源 / 7 dll / 64 库）全部校验通过

不含 Java 运行时，也不含 Forge/Fabric 等加载器；装完用 PCL2/HMCL 指向该目录即可启动。

## 直接启动游戏 + 三种登录（v2.3）
MC 窗口下方新增「账号 / Java / 内存 / 启动游戏」：

- **离线登录**：填昵称，按 `OfflinePlayer:<name>` 生成 UUID，`userType=legacy`
- **第三方登录**：任意 authlib 皮肤站（LittleSkin 等），走 `/authserver/authenticate`，
  启动时自动下载 authlib-injector（yushi.moe 失败自动切 BMCL 镜像），带 `-javaagent`、
  `-Dauthlibinjector.side=client` 与 prefetched 元数据
- **微软正版**：device code 流程（client_id 与 PCL2 相同），
  设备码 → 轮询 token → Xbox Live → XSTS → `login_with_xbox` → 校验拥有 Java 版 → 取皮肤名/UUID；
  刷新令牌保存在本地，下次启动自动刷新，无需重复登录
- **启动**：读取版本 JSON，按 Windows x64 规则拼 classpath、natives，支持新版 `arguments.jvm/game`
  与老版 `minecraftArguments`，占位符全部替换；自动探测 Java（注册表 / Program Files / JAVA_HOME / PATH），
  版本要求不足时自动换更合适的 Java；内存可选 1~8 GB
- 第三方/正版令牌存 `%APPDATA%\PCLDownloader\account.json`，Java 与内存在 `launch.tsv`
- 实测：1.7.10、b1.7.3、1.20.1 生成命令无残留 `${}`；微软 device code 接口真实返回设备码

## 在线更新（v2.0 新增）

程序自带更新器，直接从本仓库的 Releases 拉取新版本：

- 启动 3 秒后静默检查一次；主界面右上「检查更新」可手动检查
- 数据源：`https://api.github.com/repos/zhaozichengmc/multithread-launcher/releases/latest`，
  api.github.com 连不上时自动切 `ghproxy.net` / `gh-proxy.com` 镜像
- 版本号写在 `UpdateChecker.CurrentVersion`，与最新 tag（如 `V2.1`）逐段比较
- 有新版时弹窗显示更新说明，可「下载并更新」：下载该版本的单文件 exe →
  批处理等本进程退出后覆盖自身 → 重启，失败自动重试 12 次
- 「打开发布页」直接跳到 `https://github.com/zhaozichengmc/multithread-launcher/releases`

## 更新日志

**V2.3（2026-11-26）— 再挖一轮并全部修掉**

- **失败不再死循环**：调度器以前会立刻把 `Failed` 当 `Idle` 重新拉起，404 地址等于无限重试；现在失败态必须用户手动重试
- **暂停全部补漏**：暂停后新任务探测完仍会自己开始下载，现在会立刻被暂停
- **取消语义统一**：取消不再留下合并好的文件；退出程序则保留分块（`CancelDeletesFiles=false`），下次续传
- **断点续传更抗损**：某段还没开始写（无临时文件）不再让整份进度作废；临时文件短于计数时按实际长度恢复
- **整段 Range 回 200 可用**：以前直接判定"服务器不支持分段"，现在当作整份响应接收
- **Range + gzip 冲突**：分段请求带 `Accept-Encoding: identity`，避免解压后偏移错位写出坏文件
- **HttpClient 复用**：按代理配置缓存共享实例，长时间多线程下载不再泄漏连接池/句柄
- **坏文件不留**：SHA-256 不匹配时删除输出文件，避免"看着下好了其实坏了"
- **更新安全**：校验 GitHub `assets.digest`（sha256）；`apply.bat` 改无 BOM UTF-8；静默检查不再弹窗
- **MC 完整性**：客户端 jar 校验 SHA-1；资源失败明确报错；所有 MC 请求带超时（含 authlib）
- **网络调优更保守**：三个 TCP 参数必须全部解析成功才动系统设置；netsh 改异步读，15 秒未返回即杀
- **浏览器兜底**：内核不可用时回车改走系统浏览器；非 http（blob/data）下载交回 WebView2
- **登录提示**：XSTS 常见错误码翻译成中文
- **跨线程回调**：MC 启动线程、浏览器、主窗口 `Ui()` 全部加 `IsHandleCreated` 与异常保护
- **文件名**：过滤 Windows 保留名与结尾点/空格；缺 `mainClass` 时明确报错

**V2.2（2026-11-25）— 一轮全量修 bug**

- **暂停/继续**：单线程任务以前一暂停就永久卡死；现在单线程/多线程都秒级响应（100ms 轮询，不再等 30s 读超时）
- **不会两条线程写同一分块**：继续时只给"还没下完且没有线程在跑"的分块派线程，慢连接线程先回收再续传，不再 IOException 抢写锁
- **重试不再产生重复字节**：每次续写先按已完成长度 `SetLength` 对齐临时文件，之前用 Append 会导致大小对、内容错
- **镜像按分块切换**：某个分块失败只换这一块的源，不再全局换源把多线程混成坏文件
- **跨进程断点续传**：分块临时文件固定到 `%TEMP%\PclDlCache\<路径MD5>`，进度每秒写 `目标.pdlstate`，关掉程序再开能接着下；合并前校验分块连续覆盖
- **限速真实生效**：低速档（1~5KB/s）不再超速到几十 KB，暂停时等待线程立即醒
- **更新包校验**：下载超时 15s、必须大于 100KB、必须等长、必须以 `MZ` 开头才替换，代理返回的 429/HTML 不会再当成 exe 覆盖自己；替换脚本改为延迟展开 + 最多重试 60 次
- **关窗不闪退**：MC 窗口、更新窗口、浏览器窗口的后台回调统一走安全 `Ui()`，控件销毁后不再 BeginInvoke 抛异常带走进程
- **队列恢复**：重启后只有"已完成"保持完成，没下完的统一回到等待；再点开始不会把已下好的文件删了重下
- **自动关机更安全**：只要有一个任务失败/取消，就跳过自动关机与休眠并写日志
- **MC 资源校验 SHA-1**：assets 本来就按 SHA-1 命名，现在大小 + 哈希都过才认，镜像返回的坏文件会被重下
- **启动参数按 Windows 规则拼接**：`minecraftArguments` 支持引号内空格与空参数，路径带空格、昵称带引号都不会错位
- **DNS 优选**：修掉 pending 多减一次导致提前返回、只取最慢结果的问题；只接受来源地址/事务 ID/QR 位都对得上的应答；不再用全局锁串行 4 秒
- **其他**：设置文件单个数值坏了不再整套丢；通知区图标 HICON 退出时 `DestroyIcon`；重复启动会提示"已在运行"；`crash.log` 超 1MB 自动轮转；速度/超时改用单调时钟，不再 49.7 天溢出

**V2.1（2026-11-25）**
**V2.1（2026-11-25）**

- 单文件版把 Newtonsoft.Json、WebView2 托管库全部内嵌（Costura），WebView2Loader.dll 释放到临时目录并加入 DLL 搜索路径，单独拷 exe 也能跑
- 修复内置浏览器地址栏回车无效：内核未就绪时先缓存地址，就绪后自动打开
- 修复 MC 下载闪退：完整版压缩包先前漏了 `Newtonsoft.Json.dll`，读版本清单就 `FileNotFoundException` 闪退
- 新增崩溃兜底：`Application.ThreadException` + `AppDomain.UnhandledException` 写 `%APPDATA%\PCLDownloader\crash.log` 并弹窗，不再静默退出
