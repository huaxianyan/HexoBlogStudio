"""生成应用图标：蓝底圆角方块 + 白色六边形（呼应 Hexo）+ 三条正文行。

用法：python tools/make-icon.py
输出：Assets/app.ico（16-256 多尺寸）、docs/icon-512.png

只改这里，不要手动编辑 ico。颜色取自主题站点 logo 的蓝，换色改 COLORS 即可。
"""

from __future__ import annotations

import math
import os

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_ICO = os.path.join(ROOT, "Assets", "app.ico")
OUT_PNG = os.path.join(ROOT, "docs", "icon-512.png")

BASE = 1024          # 母版边长
SUPERSAMPLE = 4      # 超采样倍数，缩小时更干净
BLUE_TOP = (46, 156, 246)
BLUE_BOTTOM = (11, 99, 196)
WHITE = (255, 255, 255, 255)


def rounded_background(size: int) -> Image.Image:
    """竖直渐变的圆角方块（Windows 11 图标的安全边距约 8%）。"""
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gradient = Image.new("RGBA", (1, size))
    for y in range(size):
        t = y / max(size - 1, 1)
        gradient.putpixel(
            (0, y),
            (
                round(BLUE_TOP[0] + (BLUE_BOTTOM[0] - BLUE_TOP[0]) * t),
                round(BLUE_TOP[1] + (BLUE_BOTTOM[1] - BLUE_TOP[1]) * t),
                round(BLUE_TOP[2] + (BLUE_BOTTOM[2] - BLUE_TOP[2]) * t),
                255,
            ),
        )
    gradient = gradient.resize((size, size))

    margin = round(size * 0.06)
    radius = round(size * 0.22)
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (margin, margin, size - margin - 1, size - margin - 1), radius=radius, fill=255
    )
    canvas.paste(gradient, (0, 0), mask)
    return canvas


def hexagon(cx: float, cy: float, radius: float, rotation: float = math.pi / 6) -> list[tuple[float, float]]:
    return [
        (
            cx + radius * math.cos(rotation + i * math.pi / 3),
            cy + radius * math.sin(rotation + i * math.pi / 3),
        )
        for i in range(6)
    ]


def draw_icon(size: int) -> Image.Image:
    s = size
    canvas = rounded_background(s)
    draw = ImageDraw.Draw(canvas)

    cx = cy = s / 2
    # 六边形轮廓：留出内边距，保证 16px 下也不贴边
    radius = s * 0.30
    stroke = max(round(s * 0.055), 2)
    draw.polygon(hexagon(cx, cy, radius), outline=WHITE, width=stroke)

    # 里面的三条正文行，宽度递减更像文章
    bar_h = max(round(s * 0.042), 2)
    gap = s * 0.105
    widths = (0.30, 0.30, 0.19)
    for index, width_ratio in enumerate(widths):
        y = cy + (index - 1) * gap
        half = s * width_ratio / 2
        draw.rounded_rectangle(
            (cx - half, y - bar_h / 2, cx + half, y + bar_h / 2),
            radius=bar_h / 2,
            fill=WHITE,
        )
    return canvas


def main() -> None:
    big = draw_icon(BASE * SUPERSAMPLE).resize((BASE, BASE), Image.LANCZOS)

    os.makedirs(os.path.dirname(OUT_ICO), exist_ok=True)
    os.makedirs(os.path.dirname(OUT_PNG), exist_ok=True)

    sizes = [256, 128, 64, 48, 32, 24, 16]
    frames = [big.resize((n, n), Image.LANCZOS) for n in sizes]
    frames[0].save(OUT_ICO, format="ICO", sizes=[(n, n) for n in sizes])

    big.resize((512, 512), Image.LANCZOS).save(OUT_PNG)
    print(f"已生成 {OUT_ICO}（{', '.join(f'{n}px' for n in sizes)}）与 {OUT_PNG}")


if __name__ == "__main__":
    main()
