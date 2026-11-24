#!/usr/bin/env python3
# MC 下载窗口渲染图
from PIL import Image, ImageDraw, ImageFont

S = 4
FONT = "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc"
_f = {}


def F(pt):
    k = int(round(pt * S))
    if k not in _f:
        _f[k] = ImageFont.truetype(FONT, k)
    return _f[k]


f9 = F(9); f10 = F(10); f11 = F(11); f12 = F(12); f12h = F(12)

CW, CH = 840, 600
PAD = 46
W, H = CW + PAD * 2, CH + PAD * 2 + 30
img = Image.new("RGB", (W * S, H * S), (233, 238, 245))
d = ImageDraw.Draw(img)
for y in range(H * S):
    t = y / (H * S)
    d.line([(0, y), (W * S, y)], fill=(int(233 + (214 - 233) * t), int(238 + (226 - 238) * t), int(245 + (236 - 245) * t)))


def P(v): return int(round(v * S))


OX, OY = PAD, PAD + 32


def text(x, y, s, font=f9, fill=(0, 0, 0)):
    d.text((P(OX + x), P(OY + y)), s, font=font, fill=fill)


def rect(x, y, w, h, fill=None, outline=None, width=1, r=0):
    box = [P(OX + x), P(OY + y), P(OX + x + w - 1), P(OY + y + h - 1)]
    if r:
        d.rounded_rectangle(box, radius=P(r), fill=fill, outline=outline, width=P(width))
    else:
        d.rectangle(box, fill=fill, outline=outline, width=P(width))


def grad_round(x, y, w, h, r, c1, c2, border=None):
    im = Image.new("RGB", (P(w), P(h)))
    dd = ImageDraw.Draw(im)
    for yy in range(P(h)):
        t = yy / max(1, P(h))
        dd.line([(0, yy), (P(w), yy)], fill=(int(c1[0] + (c2[0] - c1[0]) * t),
                                            int(c1[1] + (c2[1] - c1[1]) * t),
                                            int(c1[2] + (c2[2] - c1[2]) * t)))
    mask = Image.new("L", (P(w), P(h)), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, P(w) - 1, P(h) - 1], radius=P(r), fill=255)
    img.paste(im, (P(OX + x), P(OY + y)), mask)
    if border:
        d.rounded_rectangle([P(OX + x), P(OY + y), P(OX + x + w - 1), P(OY + y + h - 1)],
                            radius=P(r), outline=border, width=P(1))


def centered(cx, cy, w, h, s, color, maxpt):
    size = maxpt
    while size > 6:
        f = F(size)
        bb = d.textbbox((0, 0), s, font=f)
        if bb[2] - bb[0] <= (w - 10) * S:
            break
        size -= 0.5
    f = F(size)
    bb = d.textbbox((0, 0), s, font=f)
    x = cx + (w * S - (bb[2] - bb[0])) / 2 - bb[0]
    y = cy + (h * S - (bb[3] - bb[1])) / 2 - bb[1]
    d.text((x, y), s, font=f, fill=color)


def btn(x, y, w, h, s, primary=False, disabled=False):
    if primary:
        grad_round(x, y, w, h, 7, (0, 132, 216), (0, 102, 188), None)
        centered(P(OX + x), P(OY + y), w, h, s, (248, 252, 255), 13.5)
    else:
        grad_round(x, y, w, h, 7, (252, 253, 255), (237, 241, 246), (211, 215, 221))
        centered(P(OX + x), P(OY + y), w, h, s, (25, 25, 25), 13)


# 阴影 + 窗口
for i in range(20, 0, -1):
    a = int(24 * (i / 20))
    d.rectangle([P(OX - i / 2), P(OY - i / 2 + 10), P(OX + CW + i / 2), P(OY + CH + i / 2 + 12)],
                outline=(a + 200, a + 206, a + 214), width=1)
rect(0, -32, CW, CH + 32, fill=(240, 240, 240), outline=(150, 150, 150))
rect(1, -31, CW - 2, 31, fill=(255, 255, 255))
icon = Image.open("/tmp/app_icon.png").convert("RGBA").resize((P(20), P(20)), Image.LANCZOS)
img.paste(icon, (P(OX + 12), P(OY - 25)), icon)
text(38, -23, "下载 Minecraft 原版", f10)
for i, g in enumerate(["─", "▢", "×"]):
    text(CW - 48 + i * 16, -25, g, f11)

# 控件区从 y=0 起按客户区坐标
CL = 0
# 版本类型
text(16, 14, "版本类型", f9)
rect(80, 11, 110, 24, fill=(255, 255, 255), outline=(122, 122, 122))
text(86, 15, "全部", f9)
d.polygon([(P(OX + 172), P(OY + 18)), (P(OX + 182), P(OY + 18)), (P(OX + 177), P(OY + 24))], fill=(70, 70, 70))
rect(316, 11, 118, 24, fill=(255, 255, 255), outline=(122, 122, 122))
text(322, 15, "最新在前", f9)
d.polygon([(P(OX + 408), P(OY + 18)), (P(OX + 418), P(OY + 18)), (P(OX + 413), P(OY + 24))], fill=(70, 70, 70))
btn(444, 10, 96, 26, "刷新清单")
# 列表
rect(16, 46, 808, 240, fill=(255, 255, 255), outline=(122, 122, 122))
# 表头
d.line([P(OX + 17), P(OY + 46 + 24), P(OX + 823), P(OY + 46 + 24)], fill=(226, 226, 226), width=P(1))
text(24, 51, "版本", f9, (50, 50, 50))
text(344, 51, "类型", f9, (50, 50, 50))
text(464, 51, "发布时间", f9, (50, 50, 50))
rows = [("26.3", "正式版", "2026-11-18 05:12:00"), ("26.4-snapshot-10", "快照", "2026-11-22 06:44:00"),
        ("26.2", "正式版", "2026-10-21 04:58:00"), ("26.4-snapshot-9", "快照", "2026-11-19 07:02:00"),
        ("26.1", "正式版", "2026-09-16 05:31:00"), ("1.21.9", "正式版", "2026-09-30 05:20:00"),
        ("1.7.10", "正式版", "2014-05-14 21:29:00"), ("b1.7.3", "Beta 远古", "2011-07-08 21:29:00")]
sel = 0
for i, (vid, vt, vd) in enumerate(rows):
    y = 46 + 26 + i * 22
    if i == sel:
        rect(17, y - 2, 806, 22, fill=(0, 120, 212))
    col = (255, 255, 255) if i == sel else (20, 20, 20)
    text(24, y, vid, f9, col)
    text(344, y, vt, f9, col)
    text(464, y, vd, f9, (235, 240, 246) if i == sel else (70, 70, 70))
# 目录
text(16, 300, "游戏目录", f9)
rect(80, 297, 620, 24, fill=(255, 255, 255), outline=(122, 122, 122))
text(86, 301, r"C:\Users\Administrator\AppData\Roaming\.minecraft", f9, (40, 40, 40))
btn(708, 295, 60, 26, "浏览…")
btn(772, 295, 52, 26, "打开")
# 进度
rect(16, 334, 808, 18, fill=(235, 235, 235), outline=(180, 180, 180))
rect(17, 335, 700, 16, fill=(0, 120, 212), outline=None)
text(360, 336, "78%", f9, (255, 255, 255))
text(16, 358, "资源文件 2810/3600（失败 0）· 线程 8 · 12.4 MB/s", f9, (110, 110, 110))
# 按钮
btn(16, 386, 132, 32, "下载此版本", primary=True)
btn(156, 386, 88, 32, "取消", )
# 日志
rect(16, 430, 808, 150, fill=(250, 250, 250), outline=(170, 170, 170))
logs = [
 "20:31:02  清单已加载：925 个版本（正式 103 · 快照 761 · 远古 61）",
 "20:31:05  客户端 jar 完成：22 MB",
 "20:31:11  支持库完成：64 个",
 "20:31:12  资源清单：3600 个文件（缺失的才下）",
 "20:31:44  资源文件 1200/3600（失败 0）",
 "20:32:18  资源文件 2810/3600（失败 0）",
]
# 列表右侧滚动条
rect(816, 72, 7, 206, fill=(241, 241, 241), outline=(200, 200, 200))
rect(817, 76, 5, 56, fill=(193, 193, 193))
for i, s in enumerate(logs):
    text(22, 435 + i * 21, s, f9, (60, 60, 60))

img = img.resize((W * 2, H * 2), Image.LANCZOS)
img.save("preview/MC下载窗口.png")
print(img.size)
