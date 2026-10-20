#!/usr/bin/env bash
#
# 把 .app 打成 .dmg：打开后是「Astra Pet.app」和一个指向「应用程序」的快捷方式，拖过去就是安装。
# 用法：tools/package/make_dmg.sh <Astra Pet.app> <输出.dmg>
# 签名和公证在 dmg 做好之后由发布工作流完成（见 .github/workflows/release.yml 和 notarize.sh）。
set -euo pipefail
APP="${1:?用法: make_dmg.sh <.app> <输出.dmg>}"
OUT="${2:?用法: make_dmg.sh <.app> <输出.dmg>}"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
ditto "$APP" "$STAGE/$(basename "$APP")"      # ditto 保留签名、扩展属性和符号链接
ln -s /Applications "$STAGE/Applications"

rm -f "$OUT"
hdiutil create -volname "Astra Pet" -srcfolder "$STAGE" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$OUT" > /dev/null
echo "已生成 $OUT ($(du -h "$OUT" | cut -f1))"
