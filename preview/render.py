#!/usr/bin/env python3
# 1 px = 2 输出像素；先 4 倍画再 LANCZOS 缩，圆角平滑
from PIL import Image, ImageDraw, ImageFont

S = 4
OUT = 2


def P(v):
    return int(round(v * S))


W, H = 1900, 980
img = Image.new("RGB", (W * S, H * S), (238, 242, 247))
d = ImageDraw.Draw(img)

for y in range(H * S):
    t = y / (H * S)
    c = (int(238 + (221 - 238) * t), int(242 + (228 - 242) * t), int(247 + (238 - 247) * t))
    d.line([(0, y), (W * S, y)], fill=c)

FONT = "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc"
_cache = {}


def F(pt):
    k = int(round(pt * S / 2.0 * 1.0)) if False else int(round(pt * (S / 1.0)))
    k = int(round(pt * S))
    if k not in _cache:
        _cache[k] = ImageFont.truetype(FONT, k)
    return _cache[k]


f12 = F(9)
f13 = F(9.5)
f11 = F(8.5)
f8 = F(7)


def text(x, y, s, font=f12, fill=(0, 0, 0)):
    d.text((P(x), P(y)), s, font=font, fill=fill)


def rect(x, y, w, h, fill=None, outline=None, width=1):
    d.rectangle([P(x), P(y), P(x + w - 1), P(y + h - 1)], fill=fill, outline=outline, width=P(width))


# ---------- 窗口 ----------
WX, WY, WW, WH = 40, 50, 1008, 793
for i in range(18, 0, -1):
    a = int(26 * (i / 18))
    d.rectangle([P(WX - i / 2), P(WY - i / 2 + 6), P(WX + WW + i / 2), P(WY + WH + i / 2 + 8)],
                outline=(a + 200, a + 205, a + 212), width=1)
rect(WX, WY, WW, WH, fill=(240, 240, 240), outline=(154, 154, 154))
rect(WX + 1, WY + 1, WW - 2, 31, fill=(255, 255, 255))
icon = Image.open("/tmp/app_icon.png").convert("RGBA").resize((P(18), P(18)), Image.LANCZOS)
img.paste(icon, (P(WX + 12), P(WY + 7)), icon)
text(WX + 36, WY + 9, "光速下载器", f12)
d.line([P(WX + 1), P(WY + 31), P(WX + WW - 2), P(WY + 31)], fill=(216, 216, 216), width=P(1))

for i, g in enumerate(["─", "▢", "×"]):
    bx = WX + WW - 2 - 46 * (3 - i)
    text(bx + 15, WY + 7 if i < 2 else WY + 6, g, f11 if i < 2 else f13, (0, 0, 0))

CX, CY = WX + 1, WY + 32
CW, CH = 980, 730
rect(CX, CY, CW, CH, fill=(240, 240, 240))

RADIUS = 8


def grad_round(x, y, w, h, r, c1, c2, border):
    im = Image.new("RGB", (P(w), P(h)))
    dd = ImageDraw.Draw(im)
    for yy in range(P(h)):
        t = yy / max(1, P(h))
        dd.line([(0, yy), (P(w), yy)], fill=(int(c1[0] + (c2[0] - c1[0]) * t),
                                            int(c1[1] + (c2[1] - c1[1]) * t),
                                            int(c1[2] + (c2[2] - c1[2]) * t)))
    mask = Image.new("L", (P(w), P(h)), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, P(w) - 1, P(h) - 1], radius=P(r), fill=255)
    img.paste(im, (P(CX + x), P(CY + y)), mask)
    if border:
        d.rounded_rectangle([P(CX + x), P(CY + y), P(CX + x + w - 1), P(CY + y + h - 1)],
                            radius=P(r), outline=border, width=P(1))


def draw_centered(cx, cy, w, h, s, color, max_pt):
    size = max_pt
    while size > 6:
        f = F(size)
        bb = d.textbbox((0, 0), s, font=f)
        if bb[2] - bb[0] <= (w - 12) * S:
            break
        size -= 0.5
    f = F(size)
    bb = d.textbbox((0, 0), s, font=f)
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    x = cx + (w * S - tw) / 2 - bb[0]
    y = cy + (h * S - th) / 2 - bb[1]
    d.text((x, y), s, font=f, fill=color)


def btn(x, y, w, h, s, primary=False, disabled=False):
    if primary:
        if disabled:
            grad_round(x, y, w, h, RADIUS, (208, 228, 247), (199, 223, 245), (199, 223, 245))
        else:
            grad_round(x, y, w, h, RADIUS, (0, 132, 216), (0, 104, 190), None)
        draw_centered(P(CX + x), P(CY + y), w, h, s, (108, 143, 175) if disabled else (247, 251, 255), 13)
        return
    if disabled:
        grad_round(x, y, w, h, RADIUS, (244, 244, 244), (243, 243, 243), (226, 226, 226))
    else:
        grad_round(x, y, w, h, RADIUS, (252, 253, 255), (236, 240, 246), (214, 217, 222))
    draw_centered(P(CX + x), P(CY + y), w, h, s, (166, 166, 166) if disabled else (26, 26, 26), 12.5)


def tb(x, y, w, h, lines, disabled=False):
    rect(CX + x, CY + y, w, h, fill=(236, 236, 236) if disabled else (255, 255, 255), outline=(122, 122, 122))
    for i, s in enumerate(lines):
        text(CX + x + 5, CY + y + 4 + i * 17, s, f12, (109, 109, 109) if disabled else (0, 0, 0))


def lab(s, x, y, color=(0, 0, 0), font=f12, w=None):
    text(CX + x, CY + y, s, font, color)


def check(s, x, y, on=False, w=120):
    rect(CX + x, CY + y + 3, 11, 11, fill=(255, 255, 255), outline=(51, 51, 51))
    if on:
        d.line([P(CX + x + 2), P(CY + y + 9), P(CX + x + 4.5), P(CY + y + 12), P(CX + x + 9), P(CY + y + 5)],
               fill=(0, 120, 212), width=2)
    lab(s, x + 16, y, (60, 60, 60))
    return w


# ---------------- 顶部 ----------------
lab("下载地址（每行一个，可批量粘贴 / 拖入链接）", 16, 10)
rect(CX + 16, CY + 30, 758, 56, fill=(255, 255, 255), outline=(122, 122, 122))
lab("https://mirrors.aliyun.com/win10/Win10_22H2_ChnSimp_x64.iso", 21, 34, (0, 0, 0), f11)
lab("https://mirrors.aliyun.com/office/Office2021_x64.iso", 21, 51, (0, 0, 0), f11)
lab("https://mirrors.aliyun.com/driver/HP_LaserJet_103w.exe", 21, 68, (0, 0, 0), f11)

btn(788, 30, 176, 26, "添加到队列", primary=True)
btn(788, 60, 176, 26, "粘贴并添加")

lab("保存到（文件夹）", 16, 94)
rect(CX + 16, CY + 112, 758, 22, fill=(255, 255, 255), outline=(122, 122, 122))
lab("D:\\Downloads", 21, 115)
btn(788, 111, 84, 24, "浏览…")
btn(876, 111, 88, 24, "打开目录")

# ---------------- 选项行 ----------------
lab("线程数", 16, 148)
rect(CX + 72, CY + 145, 58, 24, fill=(255, 255, 255), outline=(122, 122, 122))
lab("16", 90, 148)
rect(CX + 72 + 40, CY + 146, 17, 22, fill=(242, 242, 242), outline=(160, 160, 160))
lab("▲", 112, 145, (122, 122, 122), f8)
lab("▼", 112, 155, (122, 122, 122), f8)

lab("并发任务", 136, 148)
rect(CX + 200, CY + 145, 52, 24, fill=(255, 255, 255), outline=(122, 122, 122))
lab("2", 214, 148)
lab("限速", 260, 148)
rect(CX + 298, CY + 145, 64, 24, fill=(255, 255, 255), outline=(122, 122, 122))
lab("0", 304, 148)
lab("KB/s（0=不限）", 366, 148, (128, 128, 128))

check("自动优选 DNS", 476, 145, True)
check("网络调优", 594, 145, True)
check("监听剪贴板链接", 684, 145, False)

# ---------------- 按钮行 ----------------
btn(16, 178, 104, 32, "开始全部", primary=True)
btn(112, 178, 96, 32, "暂停全部", disabled=False)
btn(216, 178, 92, 32, "取消全部")
btn(316, 178, 92, 32, "重试失败")
btn(416, 178, 100, 32, "清空已完成")
btn(524, 178, 84, 32, "设置…")
btn(616, 178, 96, 32, "浏览网页", primary=True)

# ---------------- 任务列表 ----------------
LX, LY, LW, LH = 16, 220, 948, 252
rect(CX + LX, CY + LY, LW, LH, fill=(255, 255, 255), outline=(122, 122, 122))
rect(CX + LX + 1, CY + LY + 1, LW - 2, 22, fill=(244, 246, 249), outline=(216, 219, 224))
cols = [("文件名", 360), ("大小", 100), ("进度", 80), ("速度", 110), ("状态", 160), ("线程", 70)]
cx0 = LX + 5
for name, cw in cols:
    text(CX + cx0, CY + LY + 5, name, f11, (60, 60, 60))
    cx0 += cw
    d.line([P(CX + cx0 - 5), P(CY + LY + 1), P(CX + cx0 - 5), P(CY + LY + LH - 1)], fill=(228, 228, 228), width=1)

rows = [
    ("Win10_22H2_ChnSimp_x64.iso", "5.3 GB", "100%", "—", "已完成", "16 线", (6, 176, 37)),
    ("Office2021_x64.iso", "4.1 GB", "44%", "11.7 MB/s", "下载中 44%", "16/16", (0, 110, 200)),
    ("HP_LaserJet_103w.exe", "58 MB", "0%", "—", "等待开始", "8 线", (120, 120, 120)),
]
ry = LY + 24
for name, size, pct, spd, st, th, col in rows:
    if ry + 22 > LY + LH:
        break
    cx0 = LX + 5
    vals = [name, size, pct, spd, st, th]
    for i, ((_, cw), v) in enumerate(zip(cols, vals)):
        text(CX + cx0, CY + ry + 4, v, f11,
             col if i == 4 else (0, 0, 0))
        cx0 += cw
    d.line([P(CX + LX + 1), P(CY + ry + 22), P(CX + LX + LW - 2), P(CY + ry + 22)], fill=(235, 235, 235), width=1)
    ry += 23

# 空行留白（真实控件也是白底）
# ---------------- 日志 ----------------
lab("日志", 16, 478, (128, 128, 128))
rect(CX + 16, CY + 496, 948, 150, fill=(250, 250, 250), outline=(122, 122, 122))
log_lines = [
    "21:58:03  网络调优：当前 autotuning=normal，rss=enabled，timestamps=disabled",
    "21:58:03  网络调优：已应用（窗口自动调优→normal），下载结束自动还原",
    "21:58:04  [Office2021_x64.iso] DNS 优选结果：阿里 DNS（223.5.5.5）→ 121.43.184.7",
    "21:58:04  [Office2021_x64.iso] 文件大小 4.1 GB，服务器支持分段，最多 16 线程",
    "21:58:05  [Win10_22H2_ChnSimp_x64.iso] SHA-256 校验通过",
    "21:58:06  [Office2021_x64.iso] 线程 #9 切换镜像源：https://mirror.example.com/office.iso",
    "21:58:07  [Office2021_x64.iso] 已用 16 线程，平均 11.7 MB/s，进度 44%",
]
ly = 502
for s in log_lines:
    if ly > 496 + 150 - 14:
        break
    lab(s, 21, ly, (40, 40, 40), f11)
    ly += 17

# ---------------- 底栏 ----------------
lab("完成后", 16, 664)
rect(CX + 68, CY + 659, 140, 24, fill=(255, 255, 255), outline=(122, 122, 122))
lab("不操作", 74, 663)
lab("队列 3 个 · 运行 1 · 总速度 11.7 MB/s 44%", 220, 664, (128, 128, 128))
btn(790, 658, 90, 28, "打开文件")
btn(888, 658, 92, 28, "导出日志")


# ---------------- 内置浏览器窗口 ----------------
BX, BY, BW, BH = 1088, 196, 772, 566
for i in range(16, 0, -1):
    a = int(24 * (i / 16))
    d.rectangle([P(BX - i / 2), P(BY - i / 2 + 5), P(BX + BW + i / 2), P(BY + BH + i / 2 + 6)],
                outline=(a + 202, a + 206, a + 212), width=1)
rect(BX, BY, BW, BH, fill=(240, 240, 240), outline=(154, 154, 154))
rect(BX + 1, BY + 1, BW - 2, 29, fill=(255, 255, 255))
icon2 = Image.open("/tmp/app_icon.png").convert("RGBA").resize((P(15), P(15)), Image.LANCZOS)
img.paste(icon2, (P(BX + 9), P(BY + 8)), icon2)
text(BX + 30, BY + 8, "光速下载器 · 内置浏览器", f12)
text(BX + BW - 24, BY + 6, "×", f13)
d.line([P(BX + 1), P(BY + 29), P(BX + BW - 2), P(BY + 29)], fill=(216, 216, 216), width=P(1))

# 工具栏
rect(BX + 1, BY + 30, BW - 2, 34, fill=(248, 249, 251))
for i, g in enumerate(["←", "→"]):
    rect(BX + 7 + i * 30, BY + 36, 26, 22, fill=(252, 253, 255), outline=(214, 217, 222))
    text(BX + 16 + i * 30, BY + 38, g, f12, (60, 60, 60))
rect(BX + 70, BY + 36, 46, 22, fill=(252, 253, 255), outline=(214, 217, 222))
text(BX + 78, BY + 38, "刷新", f11, (60, 60, 60))
rect(BX + 124, BY + 36, 470, 22, fill=(255, 255, 255), outline=(122, 122, 122))
text(BX + 129, BY + 38, "https://mirrors.example.com/windows/", f11, (30, 30, 30))
rect(BX + 602, BY + 36, 60, 22, fill=(252, 253, 255), outline=(214, 217, 222))
text(BX + 613, BY + 38, "打开", f11)
rect(BX + 668, BY + 36, 96, 22, fill=(0, 120, 212), outline=(0, 120, 212))
text(BX + 681, BY + 38, "下载此页", f11, (255, 255, 255))

# 网页内容
rect(BX + 1, BY + 64, BW - 2, BH - 90, fill=(255, 255, 255))
rect(BX + 1, BY + 64, BW - 2, 58, fill=(244, 247, 251))
text(BX + 18, BY + 76, "Windows 镜像仓库", F(10.5), (22, 22, 22))
text(BX + 18, BY + 96, "原版 ISO 直链 · 支持断点续传 · 点击右侧按钮自动进下载队列", f11, (110, 118, 128))

rows = [
    ("Win10_22H2_ChnSimp_x64.iso", "5.32 GB", "下载", False),
    ("Win11_23H2_ChnSimp_x64.iso", "6.14 GB", "下载", True),
    ("Office2021_x64_ChnSimp.iso", "4.18 GB", "下载", False),
]
ry = BY + 132
for name, size, btn_txt, hot in rows:
    rect(BX + 16, ry, BW - 32, 64, fill=(250, 251, 253) if not hot else (238, 245, 253),
         outline=(224, 227, 232) if not hot else (0, 120, 212))
    text(BX + 32, ry + 12, name, f12, (24, 24, 24))
    text(BX + 32, ry + 34, size + "   ·   ISO 镜像   ·   校验值 SHA-256", f11, (120, 124, 130))
    rect(BX + BW - 112, ry + 17, 76, 30, fill=(0, 120, 212) if not hot else (0, 104, 190))
    text(BX + BW - 92, ry + 22, btn_txt, f12, (255, 255, 255))
    ry += 76

# 拦截提示
rect(BX + 20, BY + BH - 88, BW - 40, 34, fill=(232, 246, 236), outline=(140, 200, 158))
text(BX + 32, BY + BH - 81, "已拦截下载，加入队列：Win11_23H2_ChnSimp_x64.iso（带 Cookie）", f11, (28, 110, 56))

# 状态栏
rect(BX + 1, BY + BH - 40, BW - 2, 22, fill=(246, 247, 249))
d.ellipse([P(BX + 10), P(BY + BH - 33), P(BX + 18), P(BY + BH - 25)], fill=(0, 160, 70))
text(BX + 24, BY + BH - 35, "就绪：网页里的下载会自动进队列", f11, (90, 96, 104))

img = img.resize((W * OUT, H * OUT), Image.LANCZOS)
img.save("/home/user/Doubao/chats/38455372129916418/PCL多线程下载器/preview/界面预览.png")
print("saved", img.size)
