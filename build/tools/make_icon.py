#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
视频展台 · 图标生成器（Python 实现）

用途
────
与同目录的 make-icon.ps1 等价，用于**非 Windows 环境**（CI / Linux / macOS）
按品牌色重新生成应用图标：

    VideoPresenter.ico   7 个尺寸（16 / 24 / 32 / 48 / 64 / 128 / 256）
    AppIcon.png          标题栏用 64×64

设计
────
* 纯标准库（struct / zlib / os），无 Pillow 依赖
* 手写 PNG 编码 + ICO 容器封装（内嵌 PNG，Vista+ 原生支持）
* 3×3 超采样抗锯齿
* Win11 强调色对角渐变（#0067C0 → #4CC2FF）+ 白色摄像机剪影

用法
────
    python build/tools/make_icon.py
    python build/tools/make_icon.py --out ./custom-assets

开发商：UNSA Studio
"""

import argparse
import os
import struct
import zlib

# ── 品牌色（Windows 11 强调蓝）──────────────────────────────────────────────
GRADIENT_FROM = (0.0, 103.0, 192.0)     # #0067C0
GRADIENT_TO = (76.0, 194.0, 255.0)      # #4CC2FF
CORNER_RADIUS = 0.22                    # 相对边长的圆角半径


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def inside_round_rect(px: float, py: float, rn: float) -> bool:
    """归一化坐标下的圆角矩形命中测试。"""
    cx = min(max(px, rn), 1.0 - rn)
    cy = min(max(py, rn), 1.0 - rn)
    dx = px - cx
    dy = py - cy
    return dx * dx + dy * dy <= rn * rn


def inside_camera(px: float, py: float) -> bool:
    """白色摄像机剪影（机身 + 取景条 + 镜头）。"""
    # 机身
    if 0.20 <= px <= 0.66 and 0.34 <= py <= 0.66:
        return True
    # 顶部取景条
    if 0.28 <= px <= 0.48 and 0.25 <= py <= 0.34:
        return True
    # 右侧镜头（梯形）
    if 0.66 <= px <= 0.86:
        t = (px - 0.66) / 0.20
        y0 = 0.38 - 0.08 * t
        y1 = 0.62 + 0.08 * t
        if y0 <= py <= y1:
            return True
    return False


def render(size: int, ss: int = 3) -> bytes:
    """渲染为 straight-alpha 的 RGBA 字节流。"""
    inv = 1.0 / size
    buf = bytearray(size * size * 4)
    samples = float(ss * ss)

    for y in range(size):
        for x in range(size):
            sr = sg = sb = sa = 0.0
            for sy in range(ss):
                for sx in range(ss):
                    px = (x + (sx + 0.5) / ss) * inv
                    py = (y + (sy + 0.5) / ss) * inv

                    if not inside_round_rect(px, py, CORNER_RADIUS):
                        continue

                    if inside_camera(px, py):
                        r, g, b = 255.0, 255.0, 255.0
                    else:
                        t = (px + py) * 0.5
                        r = lerp(GRADIENT_FROM[0], GRADIENT_TO[0], t)
                        g = lerp(GRADIENT_FROM[1], GRADIENT_TO[1], t)
                        b = lerp(GRADIENT_FROM[2], GRADIENT_TO[2], t)

                    sr += r
                    sg += g
                    sb += b
                    sa += 1.0

            a = sa / samples
            if sa > 0:
                r, g, b = sr / sa, sg / sa, sb / sa
            else:
                r = g = b = 0.0

            i = (y * size + x) * 4
            buf[i] = int(r + 0.5)
            buf[i + 1] = int(g + 0.5)
            buf[i + 2] = int(b + 0.5)
            buf[i + 3] = int(a * 255 + 0.5)

    return bytes(buf)


def png_bytes(rgba: bytes, w: int, h: int) -> bytes:
    """最小 PNG 编码器（8-bit RGBA，无滤波）。"""
    raw = bytearray()
    for y in range(h):
        raw.append(0)                                    # filter type 0
        raw += rgba[y * w * 4:(y + 1) * w * 4]

    def chunk(tag: bytes, data: bytes) -> bytes:
        body = tag + data
        return (struct.pack('>I', len(data)) + body
                + struct.pack('>I', zlib.crc32(body) & 0xFFFFFFFF))

    ihdr = struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', ihdr)
            + chunk(b'IDAT', zlib.compress(bytes(raw), 9))
            + chunk(b'IEND', b''))


def write_ico(path: str, sizes) -> int:
    images = [(s, png_bytes(render(s), s, s)) for s in sizes]
    os.makedirs(os.path.dirname(path), exist_ok=True)

    with open(path, 'wb') as f:
        f.write(struct.pack('<HHH', 0, 1, len(images)))
        offset = 6 + 16 * len(images)
        for s, data in images:
            dim = 0 if s >= 256 else s
            f.write(struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(data), offset))
            offset += len(data)
        for _, data in images:
            f.write(data)

    return os.path.getsize(path)


def write_png(path: str, size: int) -> int:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(png_bytes(render(size), size, size))
    return os.path.getsize(path)


def main():
    parser = argparse.ArgumentParser(description='生成视频展台应用图标')
    parser.add_argument('--out', default=None,
                        help='输出目录；默认写入 src/VideoPresenter.App/Assets 与 '
                             'src/VideoPresenter.Launcher/Assets')
    args = parser.parse_args()

    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

    if args.out:
        targets = [args.out]
    else:
        targets = [
            os.path.join(root, 'src', 'VideoPresenter.App', 'Assets'),
            os.path.join(root, 'src', 'VideoPresenter.Launcher', 'Assets'),
        ]

    sizes = (16, 24, 32, 48, 64, 128, 256)

    for d in targets:
        ico = os.path.join(d, 'VideoPresenter.ico')
        print('ICO  {0}  {1} bytes'.format(ico, write_ico(ico, sizes)))

    app_assets = targets[0]
    print('PNG  {0}  {1} bytes'.format(
        os.path.join(app_assets, 'AppIcon.png'),
        write_png(os.path.join(app_assets, 'AppIcon.png'), 64)))
    print('PNG  {0}  {1} bytes'.format(
        os.path.join(app_assets, 'AppIcon44.png'),
        write_png(os.path.join(app_assets, 'AppIcon44.png'), 44)))

    print('\n图标生成完毕。')


if __name__ == '__main__':
    main()
