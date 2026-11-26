光速下载器 V2.4 — 本地实例识别 + 版本继承链启动（修「本地有实例却提示未下载」）

## 这一版修的核心问题

之前判断「版本装没装」只看 `versions\<版本>\<版本>.jar` 是否存在。但 Forge / Fabric / NeoForge / 整合包这类实例，目录里通常只有 `<版本>.json`（带 `inheritsFrom`），客户端 jar 在父版本目录里，于是明明能启动也被标成「未下载」。

V2.4 的改动：

1. **本地实例扫描**：遍历 `<.minecraft>\versions\*\*.json`，每个含 json 的目录算一个实例；顺着 `inheritsFrom` 往上找 jar，找到就标「本地实例」，找不到标「本地·缺 jar」。
2. **版本列表新增「本地实例」筛选**：本地实例固定在清单最前面，双击直接启动，不会误触「下载此版本」把原版文件重下覆盖。
3. **游戏目录自动定位**：默认 `%APPDATA%\.minecraft` 里没有 `versions` 时，自动去用户目录、以及其他固定磁盘顶层含 `minecraft` / `mc` / `pcl` 的目录（含 `xxx\.minecraft`）里找真正的实例目录。
4. **启动支持继承链**：把子版本与父版本 json 合并——`libraries` 按 `name` 去重（子版本覆盖同坐标）、`arguments.jvm/game` 追加、`assetIndex`/`assets`/`javaVersion` 继承父版本。
5. **classpath 兼容 maven 坐标**：Forge 的库经常只写 `name`（`group:artifact:version[:classifier]`）而没有 `downloads.artifact.path`，现在自动换算成 `libraries\group\artifact\version\...jar`，不再漏掉 mod 加载器。
6. **natives 沿继承链回退**：子版本没有 `natives` 目录时使用父版本已解压的 dll，避免 LWJGL 找不到原生库直接崩。
7. **Java 版本要求**：改为读合并后的 `javaVersion`，子版本不写也能拿到父版本的最低 Java 要求。
8. **「下载此版本」保护**：对本地已装实例拒绝下载，防止覆盖。

> 说明：如果本地实例缺的是**父版本**的 jar（比如整合包引用的 1.20.1 没装），启动器会明确提示「本地·缺 jar」；把对应原版版本装一次就能启动。

## 下载

- 单文件：`LightspeedDownloader-V2.4.exe`（约 0.9 MB，内嵌全部依赖）
- 完整包：`LightspeedDownloader-V2.4-full.zip`（含 WebView2 三件套与使用说明）

历史版本 V1.0 / V1.1 / V1.2 / V2.0 / V2.1 / V2.2 / V2.3 全部保留，未删除任何附件。
