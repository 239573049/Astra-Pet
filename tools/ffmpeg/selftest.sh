#!/usr/bin/env bash
#
# 用一段真实的宠物素材检查 ffmpeg：程序启动时的探测命令和播放时的解码命令都要能跑通，
# 而且解出来的 RGBA 帧里确实有透明像素（普通 ffmpeg 的 VP9 解码器会把透明通道丢掉）。
# 用法：tools/ffmpeg/selftest.sh <ffmpeg 路径> <某个 .webm>
set -euo pipefail
FF="${1:?用法: selftest.sh <ffmpeg> <webm>}"
CLIP="${2:?用法: selftest.sh <ffmpeg> <webm>}"

# 1) 启动探测（PetClip.Probe）：要能从流信息行读到帧率，并且是 libvpx-vp9 + alpha 像素格式
INFO="$("$FF" -hide_banner -c:v libvpx-vp9 -i "$CLIP" -frames:v 1 -f null - 2>&1 || true)"
echo "$INFO" | grep -E "Stream #0:0.*Video" | head -1
echo "$INFO" | grep -q "libvpx-vp9" || { echo "探测输出里没有 libvpx-vp9" >&2; exit 1; }
echo "$INFO" | grep -q "yuva" || { echo "像素格式不带 alpha（yuva）" >&2; exit 1; }

# 2) 播放解码（ClipStream）：第一帧是 640x360 的 RGBA，921600 字节，里面有 alpha=0 的像素
TMP="$(mktemp)"
"$FF" -v error -nostats -c:v libvpx-vp9 -i "$CLIP" -fps_mode passthrough -frames:v 1 -f rawvideo -pix_fmt rgba - > "$TMP"
SIZE="$(wc -c < "$TMP" | tr -d ' ')"
[ "$SIZE" = "921600" ] || { echo "第一帧大小 $SIZE，应该是 921600" >&2; exit 1; }
# 输出只用 ASCII：Windows 的 Python 默认用 cp1252 打印，中文会直接抛 UnicodeEncodeError。
python3 - "$TMP" <<'PY'
import sys
d = open(sys.argv[1], "rb").read()
transparent = sum(1 for i in range(3, len(d), 4) if d[i] == 0)
total = len(d) // 4
print(f"transparent pixels {transparent}/{total} ({transparent*100//total}%)")
sys.exit(0 if transparent > total // 4 else 1)
PY
rm -f "$TMP"
echo "ffmpeg 自检通过"
