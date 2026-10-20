#!/usr/bin/env bash
#
# 构建随官方发布包分发的精简 ffmpeg（LGPL，静态链接 libvpx）。
#
# 宠物动画是带透明通道的 VP9 WebM。ffmpeg 自带的 VP9 解码器会丢掉透明通道，必须用 libvpx 的 libvpx-vp9 解码器，
# 所以普通的 ffmpeg 不行，而网上现成的完整版又大（一百多 MB）又常常是 GPL。这里从未修改的官方源码构建，
# 只打开解码本项目素材所需的最少组件：
#   输入  matroska/webm 解复用 + libvpx-vp9 解码
#   输出  rawvideo（RGBA 裸帧，程序从管道读）和 null（启动时探测用）
#   不开 --enable-gpl / --enable-nonfree，不联网，不用系统库（--disable-autodetect），所以产物是自包含的 LGPL 可执行文件。
#
# 用法：tools/ffmpeg/build.sh <输出目录>
#   macOS：需要 clang、make、pkg-config；x86_64 还需要 nasm（brew install nasm pkg-config）。
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
  MINGW*|MSYS*|CYGWIN*)
    VPX_TARGET="x86_64-win64-gcc"
    FFMPEG_TARGET=(--target-os=mingw32 --arch=x86_64)
    EXE=".exe"
    EXTRA_LDFLAGS="-static"   # 把 mingw 运行时（libwinpthread 等）也静态链进去，产物不依赖 MSYS2 的 DLL
    ;;
  *) echo "不支持的系统 $OS" >&2; exit 1 ;;
esac

fetch() { # <url> <文件>
  curl -fsSL --retry 3 "$1" -o "$2"
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
    --disable-vp8 --disable-vp9-encoder --enable-vp9-decoder
  make -j"$JOBS"
  make install
)

echo "== FFmpeg $FFMPEG_VERSION"
fetch "https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz" ffmpeg.tar.xz
mkdir ffmpeg-src && tar xJf ffmpeg.tar.xz -C ffmpeg-src --strip-components=1
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
    --enable-decoder=libvpx_vp9 --enable-parser=vp9 \
    --enable-demuxer=matroska --enable-protocol=file,pipe \
    --enable-encoder=rawvideo,wrapped_avframe --enable-muxer=rawvideo,null \
    --enable-swscale --enable-avfilter \
    --enable-filter=scale,format,null,buffer,buffersink
  make -j"$JOBS"
  make install
)

install -m 755 "$WORK/ffmpeg-install/bin/ffmpeg$EXE" "$OUT/ffmpeg$EXE"

echo "== 自检"
"$OUT/ffmpeg$EXE" -hide_banner -decoders 2>/dev/null | grep -q libvpx-vp9 || { echo "缺少 libvpx_vp9 解码器" >&2; exit 1; }
if "$OUT/ffmpeg$EXE" -version | grep -E -q -- '--enable-(gpl|nonfree|version3)'; then
  echo "这不是 LGPL 构建（configure 里有 gpl / nonfree / version3）" >&2; exit 1
fi
if [ -n "$EXE" ]; then
  # Windows：只允许依赖系统自带的 DLL
  if objdump -p "$OUT/ffmpeg$EXE" | grep "DLL Name" | grep -i -E "libwinpthread|libgcc|libstdc|libvpx|msys"; then
    echo "ffmpeg.exe 还依赖 MSYS2 的 DLL" >&2; exit 1
  fi
fi
"$OUT/ffmpeg$EXE" -version | head -3
ls -la "$OUT/ffmpeg$EXE"
