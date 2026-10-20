#!/usr/bin/env bash
#
# 打 Debian / Ubuntu 的 .deb 安装包。
# 用法：tools/package/make_deb.sh <发布目录> <ffmpeg> <版本> <输出.deb>
#
# 安装位置：
#   /opt/astra-pet/            程序、素材、自带的 ffmpeg、许可文件（程序按「自己所在的目录」找它们）
#   /usr/bin/astra-pet         指向 /opt/astra-pet/AstraPet 的符号链接
#   /usr/share/applications/   应用菜单里的启动项
#   /usr/share/icons/hicolor/  图标
set -euo pipefail
PUBLISH="${1:?用法: make_deb.sh <发布目录> <ffmpeg> <版本> <输出.deb>}"
FFMPEG="${2:?}"
VERSION="${3:?}"
OUT="${4:?}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

# deb 的版本号里预览版要用 ~，这样 0.2.0~preview.1 排在 0.2.0 前面（和 SemVer 的顺序一致）
DEB_VERSION="${VERSION/-/~}"
ARCH="$(dpkg --print-architecture)"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
chmod 755 "$STAGE"   # mktemp 建的是 700，包里的根目录必须是 755，否则普通用户进不了 /opt 之外的路径
APPDIR="$STAGE/opt/astra-pet"
mkdir -p "$APPDIR" "$STAGE/usr/bin" "$STAGE/usr/share/applications" "$STAGE/usr/share/icons/hicolor/512x512/apps" "$STAGE/usr/share/doc/astra-pet" "$STAGE/DEBIAN"

cp -R "$PUBLISH/." "$APPDIR/"
rm -rf "$APPDIR"/*.dbg "$APPDIR"/*.pdb "$APPDIR"/*.dSYM
install -m 755 "$FFMPEG" "$APPDIR/ffmpeg"
chmod 755 "$APPDIR/AstraPet"
find "$APPDIR" -type d -exec chmod 755 {} \;
find "$APPDIR" -type f ! -name AstraPet ! -name ffmpeg ! -name "*.so" -exec chmod 644 {} \;

ln -s /opt/astra-pet/AstraPet "$STAGE/usr/bin/astra-pet"
install -m 644 "$ROOT/src/Assets/icons/app.png" "$STAGE/usr/share/icons/hicolor/512x512/apps/astra-pet.png"

cat > "$STAGE/usr/share/applications/astra-pet.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Astra Pet
Name[zh_CN]=Astra Pet 桌面宠物
Comment=A desktop pet that lives on your screen
Comment[zh_CN]=住在桌面上的宠物
Exec=/opt/astra-pet/AstraPet
Icon=astra-pet
Terminal=false
Categories=Utility;
StartupWMClass=AstraPet
DESKTOP
chmod 644 "$STAGE/usr/share/applications/astra-pet.desktop"

# Debian 要求的版权文件：指向随包的许可文本，并写明代码和素材是两套许可
cat > "$STAGE/usr/share/doc/astra-pet/copyright" <<COPYRIGHT
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: Astra Pet
Source: https://github.com/239573049/Astra-Pet

Files: *
Copyright: 2026 RoutinAI (https://routin.ai)
License: MIT
 The full text is in /opt/astra-pet/licenses/LICENSE.

Files: opt/astra-pet/Assets/*
Copyright: 2026 RoutinAI (https://routin.ai)
License: LicenseRef-RoutinAI-Assets
 The pet characters and artwork are NOT covered by the MIT license; all rights reserved.
 See /opt/astra-pet/licenses/LICENSE-ASSETS.md.

Files: opt/astra-pet/ffmpeg
Copyright: the FFmpeg developers
License: LGPL-2.1+
 Built without GPL or nonfree components. See /opt/astra-pet/licenses/LGPL-2.1-FFmpeg.txt.

Third-party components keep their own licenses, see /opt/astra-pet/licenses/THIRD_PARTY_NOTICES.md.
COPYRIGHT
chmod 644 "$STAGE/usr/share/doc/astra-pet/copyright"

SIZE_KB="$(du -sk "$STAGE" | cut -f1)"
cat > "$STAGE/DEBIAN/control" <<CONTROL
Package: astra-pet
Version: $DEB_VERSION
Architecture: $ARCH
Maintainer: RoutinAI
Installed-Size: $SIZE_KB
Depends: libc6 (>= 2.31), libfontconfig1, libx11-6, libice6, libsm6
Section: utils
Priority: optional
Homepage: https://astra-gate.si/pet
Description: Desktop pet that lives on your screen
 Astra Pet is a desktop pet: it walks around, reacts to clicks and
 petting, can chat through a large language model of your choice and
 reminds you to drink water and take a break.
 .
 The source code is MIT licensed; the pet artwork is copyrighted by
 RoutinAI (see /opt/astra-pet/licenses/LICENSE-ASSETS.md).
CONTROL

dpkg-deb --root-owner-group --build "$STAGE" "$OUT" > /dev/null
echo "已生成 $OUT ($(du -h "$OUT" | cut -f1))"
dpkg-deb --info "$OUT" | sed -n '1,12p'
