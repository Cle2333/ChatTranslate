"""从一张正方形设计稿 PNG 生成应用图标资源。

用法：
    python tools/make_icon.py <设计稿.png>

产出（写到 src/ChatTranslate/Assets/）：
    app.ico       多尺寸图标（16/20/24/32/40/48/64/96/128/256），供 exe 与窗口使用
    app-1024.png  母版，留作后续重新生成（设计稿常放在临时目录，会被清理）
    app-32.png    标题栏图标（标题栏里实际渲染约 16~24 px，32 足够且缩放更锐利）

设计稿要求：正方形，卡片外为透明。脚本会**裁掉透明边距**再补成正方形 ——
设计稿通常四周留白约 10%，图标应当让卡片填满画布，否则 16 px 下内容只剩 13 px，又小又糊。

ICO 容器自己写（不用 Pillow 的 ICO 保存）：小尺寸用 BMP(DIB) 条目、256 用 PNG，
这是兼容性最好的组合（部分旧 shell 对 PNG 压缩的小尺寸条目支持不佳，
而 256 用 BMP 会让体积涨到约 256 KB）。
"""
import os
import struct
import sys

from PIL import Image

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
ASSETS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                      "src", "ChatTranslate", "Assets")


def bmp_entry(img):
    """32bpp BMP(DIB) 条目：BITMAPINFOHEADER + XOR 位图 + AND 掩码。"""
    w, h = img.size
    px = img.load()

    xor = bytearray()
    for y in range(h - 1, -1, -1):          # 自底向上
        for x in range(w):
            r, g, b, a = px[x, y]
            xor += bytes((b, g, r, a))

    row_bytes = ((w + 31) // 32) * 4        # AND 掩码每行 4 字节对齐
    and_mask = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray(row_bytes)
        for x in range(w):
            if px[x, y][3] == 0:            # 位=1 表示透明
                row[x // 8] |= 0x80 >> (x % 8)
        and_mask += row

    header = struct.pack(
        "<IiiHHIIiiII",
        40, w, h * 2, 1, 32, 0,             # biSize/宽/高×2/平面/位深/不压缩
        len(xor) + len(and_mask), 0, 0, 0, 0,
    )
    return bytes(header) + bytes(xor) + bytes(and_mask)


def png_entry(img):
    import io
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    src = sys.argv[1]
    im = Image.open(src).convert("RGBA")
    print(f"设计稿: {src}  {im.size}")

    bbox = im.getchannel("A").getbbox()
    if bbox is None:
        print("设计稿全透明，无法使用")
        return 1
    card = im.crop(bbox)
    w, h = card.size
    side = max(w, h)
    sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    sq.paste(card, ((side - w) // 2, (side - h) // 2), card)
    print(f"裁切 bbox={bbox} → {card.size} → 正方形 {sq.size}")

    os.makedirs(ASSETS, exist_ok=True)

    sq.resize((1024, 1024), Image.LANCZOS).save(os.path.join(ASSETS, "app-1024.png"))
    sq.resize((32, 32), Image.LANCZOS).save(os.path.join(ASSETS, "app-32.png"))
    print("已写 app-1024.png / app-32.png")

    frames = [(s, sq.resize((s, s), Image.LANCZOS)) for s in SIZES]
    entries = [(s, png_entry(f) if s >= 256 else bmp_entry(f)) for s, f in frames]

    ico = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for s, data in entries:
        dim = 0 if s >= 256 else s          # 目录里 256 记为 0
        ico += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        ico += data

    path = os.path.join(ASSETS, "app.ico")
    with open(path, "wb") as f:
        f.write(ico)
    print(f"\n已写 app.ico = {len(ico):,} 字节，{len(entries)} 个尺寸")

    # 回读校验，确认容器结构正确
    with open(path, "rb") as f:
        raw = f.read()
    _, _, count = struct.unpack("<HHH", raw[:6])
    for i in range(count):
        bw, bh, _, _, _, bits, nbytes, off = struct.unpack("<BBBBHHII", raw[6 + 16 * i:22 + 16 * i])
        kind = "PNG" if raw[off:off + 8] == b"\x89PNG\r\n\x1a\n" else "BMP"
        print(f"  #{i}: {bw or 256:>3}x{bh or 256:<3} {bits}bpp {nbytes:>7,} 字节 {kind}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
