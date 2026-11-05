#!/usr/bin/env bash
#
# 构建随官方发布包分发的精简 ffmpeg（LGPL，静态链接 libvpx）。
#
# 宠物动画是带透明通道的 VP9 WebM。ffmpeg 自带的 VP9 解码器会丢掉透明通道，必须用 libvpx 的 libvpx-vp9 解码器，
# 所以普通的 ffmpeg 不行，而网上现成的完整版又大（一百多 MB）又常常是 GPL。这里从未修改的官方源码构建，
# 只打开播放和导入动作（design/pet-studio/06 §5）所需的最少组件：
#   播放  matroska/webm 解复用 + libvpx-vp9 解码 → rawvideo（RGBA 裸帧，程序从管道读）、null（启动时探测用）
#   导入  mov/mp4 解复用 + h264 解码（视频模型的输出）、hevc 解码（手机录的视频；7.1 的 h264 也依赖它带进来的 aom_film_grain）、prores 解码（带透明通道的 ProRes 4444 mov）
#         → rawvideo；处理后的 RGBA 帧 → libvpx-vp9 编码（yuva420p）→ webm 封装
#   不开 --enable-gpl / --enable-nonfree，不联网，不用系统库（--disable-autodetect），所以产物是自包含的 LGPL 可执行文件。
#
# 用法：tools/ffmpeg/build.sh <输出目录>
#   macOS：需要 clang、make、pkg-config；x86_64 还需要 nasm（brew install nasm pkg-config）。
#   Linux：需要 gcc 或 clang、make、pkg-config、nasm（apt install build-essential nasm pkg-config）。
#   Windows：在 MSYS2 UCRT64 里跑，需要 mingw-w64-ucrt-x86_64-gcc、make、nasm、diffutils、pkgconf、perl。
# 产物：<输出目录>/ffmpeg（Windows 是 ffmpeg.exe）。
#
set -euo pipefail

FFMPEG_VERSION="${FFMPEG_VERSION:-7.1.1}"
LIBVPX_VERSION="${LIBVPX_VERSION:-1.15.0}"

OUT="${1:?用法: build.sh <输出目录>}"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
WORK="${WORK:-$(mktemp -d)}"
PREFIX="$WORK/prefix"
JOBS="$(sysctl -n hw.ncpu 2>/dev/null || nproc)"
mkdir -p "$PREFIX"

OS="$(uname -s)"
ARCH="$(uname -m)"
EXE=""
EXTRA_CFLAGS=""
EXTRA_LDFLAGS=""
case "$OS" in
  Darwin)
    export MACOSX_DEPLOYMENT_TARGET=12.0
    EXTRA_CFLAGS="-mmacosx-version-min=12.0"
    case "$ARCH" in
      arm64) VPX_TARGET="arm64-darwin20-gcc" ;;
      x86_64) VPX_TARGET="x86_64-darwin20-gcc" ;;
      *) echo "不支持的架构 $ARCH" >&2; exit 1 ;;
    esac
    FFMPEG_TARGET=()
    ;;
  Linux)
    case "$ARCH" in
      x86_64) VPX_TARGET="x86_64-linux-gcc" ;;
      aarch64|arm64) VPX_TARGET="arm64-linux-gcc" ;;
      *) echo "不支持的架构 $ARCH" >&2; exit 1 ;;
    esac
    FFMPEG_TARGET=()
    ;;
  MINGW*|MSYS*|CYGWIN*)
    VPX_TARGET="x86_64-win64-gcc"
    FFMPEG_TARGET=(--target-os=mingw32 --arch=x86_64)
    EXE=".exe"
    EXTRA_LDFLAGS="-static"   # 把 mingw 运行时（libwinpthread 等）也静态链进去，产物不依赖 MSYS2 的 DLL
    ;;
  *) echo "不支持的系统 $OS" >&2; exit 1 ;;
esac

fetch() { # <url> <文件>
  curl -fsSL --retry 3 --retry-all-errors --retry-delay 5 --connect-timeout 20 "$1" -o "$2"
}

echo "== libvpx $LIBVPX_VERSION ($VPX_TARGET)"
cd "$WORK"
fetch "https://github.com/webmproject/libvpx/archive/refs/tags/v${LIBVPX_VERSION}.tar.gz" libvpx.tar.gz
mkdir libvpx && tar xzf libvpx.tar.gz -C libvpx --strip-components=1
(
  cd libvpx
  ./configure --target="$VPX_TARGET" --prefix="$PREFIX" \
    --disable-shared --enable-static --enable-pic \
    --disable-examples --disable-tools --disable-docs --disable-unit-tests \
    --disable-vp8 --enable-vp9-encoder --enable-vp9-decoder
  make -j"$JOBS"
  make install
)

echo "== FFmpeg $FFMPEG_VERSION"
# ffmpeg.org 偶尔整站连不上（CI 里出现过 443 连接超时 / 被重置），所以依次尝试几个地址：
# GitHub 上的官方镜像（tag 是 n<版本>）、osuosl 镜像、最后才是 ffmpeg.org。三者内容都是同一个版本的源码。
ok=0
for url in \
  "https://github.com/FFmpeg/FFmpeg/archive/refs/tags/n${FFMPEG_VERSION}.tar.gz" \
  "https://ftp.osuosl.org/pub/blfs/conglomeration/ffmpeg/ffmpeg-${FFMPEG_VERSION}.tar.xz" \
  "https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz"; do
  echo "  尝试 $url"
  if fetch "$url" ffmpeg.tar && mkdir ffmpeg-src && tar xf ffmpeg.tar -C ffmpeg-src --strip-components=1; then ok=1; break; fi
  rm -rf ffmpeg-src ffmpeg.tar
done
[ "$ok" = 1 ] || { echo "FFmpeg 源码所有下载地址都失败" >&2; exit 1; }
(
  cd ffmpeg-src
  PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig" ./configure \
    --prefix="$WORK/ffmpeg-install" ${FFMPEG_TARGET[@]+"${FFMPEG_TARGET[@]}"} \
    --pkg-config-flags=--static \
    --extra-cflags="-I$PREFIX/include $EXTRA_CFLAGS" --extra-ldflags="-L$PREFIX/lib $EXTRA_LDFLAGS" \
    --disable-everything --disable-autodetect --disable-doc --disable-debug --disable-network \
    --disable-ffplay --disable-ffprobe --enable-ffmpeg \
    --enable-small \
    --enable-libvpx \
    --enable-decoder=libvpx_vp9,h264,hevc,prores,rawvideo --enable-parser=vp9,h264,hevc \
    --enable-demuxer=matroska,mov,rawvideo --enable-protocol=file,pipe \
    --enable-encoder=rawvideo,wrapped_avframe,libvpx_vp9 --enable-muxer=rawvideo,null,webm \
    --enable-bsf=vp9_superframe \
    --enable-swscale --enable-avfilter \
    --enable-filter=scale,format,null,buffer,buffersink
  make -j"$JOBS"
  make install
)

install -m 755 "$WORK/ffmpeg-install/bin/ffmpeg$EXE" "$OUT/ffmpeg$EXE"

echo "== 自检"
"$OUT/ffmpeg$EXE" -hide_banner -decoders 2>/dev/null | grep -q libvpx-vp9 || { echo "缺少 libvpx_vp9 解码器" >&2; exit 1; }
"$OUT/ffmpeg$EXE" -hide_banner -encoders 2>/dev/null | grep -q libvpx-vp9 || { echo "缺少 libvpx_vp9 编码器" >&2; exit 1; }
for d in h264 prores; do
  "$OUT/ffmpeg$EXE" -hide_banner -decoders 2>/dev/null | grep -q " $d " || { echo "缺少 $d 解码器" >&2; exit 1; }
done
if "$OUT/ffmpeg$EXE" -version | grep -E -q -- '--enable-(gpl|nonfree|version3)'; then
  echo "这不是 LGPL 构建（configure 里有 gpl / nonfree / version3）" >&2; exit 1
fi
if [ "$OS" = Linux ] && ldd "$OUT/ffmpeg" | grep -i -E "libvpx|libav"; then
  echo "ffmpeg 还动态依赖 libvpx 或 libav*，应该是静态链接的" >&2; exit 1
fi
if [ -n "$EXE" ]; then
  # Windows：只允许依赖系统自带的 DLL
  if objdump -p "$OUT/ffmpeg$EXE" | grep "DLL Name" | grep -i -E "libwinpthread|libgcc|libstdc|libvpx|msys"; then
    echo "ffmpeg.exe 还依赖 MSYS2 的 DLL" >&2; exit 1
  fi
fi
"$OUT/ffmpeg$EXE" -version | head -3
ls -la "$OUT/ffmpeg$EXE"
