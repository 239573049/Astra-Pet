#!/usr/bin/env bash
#
# 用一段真实的宠物素材检查 ffmpeg：程序启动时的探测命令和播放时的解码命令都要能跑通，
# 而且解出来的 RGBA 帧里确实有透明像素（普通 ffmpeg 的 VP9 解码器会把透明通道丢掉）。
# 给了 mp4 时再测导入动作用的两条命令（06 §5）：解 H.264 mp4、把带透明的 RGBA 编成 VP9 webm 再解回来检查透明通道。
# 用法：tools/ffmpeg/selftest.sh <ffmpeg 路径> <某个 .webm> [某个 H.264 .mp4]
set -euo pipefail
FF="${1:?用法: selftest.sh <ffmpeg> <webm> [mp4]}"
CLIP="${2:?用法: selftest.sh <ffmpeg> <webm> [mp4]}"
MP4="${3:-}"

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

if [ -n "$MP4" ]; then
  # 3) 导入：解 H.264 mp4 的第一帧
  TMP="$(mktemp)"
  "$FF" -v error -nostats -i "$MP4" -map 0:v:0 -an -frames:v 1 -f rawvideo -pix_fmt rgb24 - > "$TMP"
  SIZE="$(wc -c < "$TMP" | tr -d ' ')"
  [ "$SIZE" -gt 0 ] || { echo "解不出 mp4 的帧" >&2; exit 1; }
  echo "h264 first frame $SIZE bytes"
  rm -f "$TMP"

  # 4) 导入：左半透明、右半不透明的 RGBA 编成 VP9 webm，再解回来，透明通道要还在
  WORK="$(mktemp -d)"
  python3 - "$WORK/in.rgba" <<'PY'
import sys
w, h, n = 64, 36, 6
row = bytes([0, 0, 0, 0]) * (w // 2) + bytes([200, 120, 40, 255]) * (w // 2)
open(sys.argv[1], "wb").write(row * h * n)
PY
  # 和 ClipProcessor 一样从标准输入喂帧（pipe:0；写 - 要 fd 协议，构建里没开）。
  "$FF" -v error -nostats -f rawvideo -pix_fmt rgba -s 64x36 -r 24 -i pipe:0 < "$WORK/in.rgba" \
    -c:v libvpx-vp9 -pix_fmt yuva420p -crf 32 -b:v 0 -row-mt 1 -an -y "$WORK/out.webm"
  "$FF" -v error -nostats -c:v libvpx-vp9 -i "$WORK/out.webm" -frames:v 1 -f rawvideo -pix_fmt rgba - > "$WORK/back.rgba"
  python3 - "$WORK/back.rgba" <<'PY'
import sys
d = open(sys.argv[1], "rb").read()
w = 64
left = [d[(y * w + 4) * 4 + 3] for y in range(36)]
right = [d[(y * w + 60) * 4 + 3] for y in range(36)]
print(f"vp9 round trip alpha left {max(left)} right {min(right)}")
sys.exit(0 if max(left) < 16 and min(right) > 240 else 1)
PY
  rm -rf "$WORK"
fi
echo "ffmpeg 自检通过"
