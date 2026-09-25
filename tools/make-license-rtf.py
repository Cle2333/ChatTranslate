"""把仓库根目录的 LICENSE（纯文本）转成 RTF，供安装向导的许可协议页使用。

WixUI 的许可页只吃 RTF（WixUILicenseRtf 指向的文件），不认 .txt/.md。

用法：
    python tools/make-license-rtf.py
产出：
    installer/LICENSE.rtf
"""
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "LICENSE")
DST = os.path.join(ROOT, "installer", "LICENSE.rtf")

BS = chr(92)   # 反斜杠


def escape(text: str) -> str:
    """转义 RTF 的特殊字符。

    RTF 里反斜杠、花括号有语法含义，必须转义；
    反斜杠必须先转（否则会把后面才加上的转义符再转一遍）。
    """
    text = text.replace(BS, BS + BS)
    text = text.replace("{", BS + "{")
    text = text.replace("}", BS + "}")
    return text


def main() -> int:
    with open(SRC, encoding="utf-8") as f:
        raw = f.read().replace("\r\n", "\n").rstrip("\n")

    # 空行保留（RTF 里就是多一个 \par），非空行的行首缩进去掉——
    # 许可证文本的折行是排版需要，不该在对话框里显示成参差不齐的缩进
    body = [escape("" if not ln.strip() else ln.strip()) for ln in raw.split("\n")]

    rtf = (
        "{" + BS + "rtf1" + BS + "ansi" + BS + "ansicpg1252" + BS + "deff0\n"
        "{" + BS + "fonttbl{" + BS + "f0" + BS + "fswiss" + BS + "fcharset0 Segoe UI;}"
        "{" + BS + "f1" + BS + "fmodern" + BS + "fcharset0 Consolas;}}\n"
        + BS + "viewkind4" + BS + "uc1" + BS + "pard" + BS + "f0" + BS + "fs18\n"
        + (BS + "par\n").join(body)
        + "\n" + BS + "par}\n"
    )

    os.makedirs(os.path.dirname(DST), exist_ok=True)
    with open(DST, "w", encoding="ascii", newline="") as f:
        f.write(rtf)

    print(f"已生成 {os.path.relpath(DST, ROOT)}")
    print(f"  源 LICENSE {len(raw)} 字符 → RTF {len(rtf)} 字节")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
