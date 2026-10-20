#!/usr/bin/env bash
#
# 公证并钉票据：tools/package/notarize.sh <.app 或 .dmg>
#
# 凭据从环境变量里取，二选一（都没有就跳过，不算失败）：
#   A. App Store Connect API 密钥：APPLE_API_KEY_P8_BASE64、APPLE_API_KEY_ID、APPLE_API_ISSUER_ID
#   B. Apple ID：APPLE_ID、APPLE_APP_PASSWORD（App 专用密码）、MACOS_SIGN_IDENTITY（团队 ID 从签名身份的括号里取）
# 提交的对象必须已经用 Developer ID 签过名。.app 会先打成 zip 再提交；.dmg 直接提交。
set -euo pipefail
TARGET="${1:?用法: notarize.sh <.app 或 .dmg>}"
TMP="${RUNNER_TEMP:-$(mktemp -d)}"

ARGS=()
if [ -n "${APPLE_API_KEY_P8_BASE64:-}" ] && [ -n "${APPLE_API_KEY_ID:-}" ] && [ -n "${APPLE_API_ISSUER_ID:-}" ]; then
  echo "$APPLE_API_KEY_P8_BASE64" | base64 --decode > "$TMP/AuthKey.p8"
  ARGS=(--key "$TMP/AuthKey.p8" --key-id "$APPLE_API_KEY_ID" --issuer "$APPLE_API_ISSUER_ID")
elif [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_APP_PASSWORD:-}" ] && [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
  TEAM_ID="$(echo "$MACOS_SIGN_IDENTITY" | sed -E 's/.*\(([A-Z0-9]+)\)$/\1/')"
  ARGS=(--apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$TEAM_ID")
else
  echo "::notice::没有公证凭据，跳过对 $(basename "$TARGET") 的公证。用户首次打开需要在「系统设置 → 隐私与安全性」里点「仍要打开」。"
  exit 0
fi

SUBMIT="$TARGET"
if [ -d "$TARGET" ]; then
  SUBMIT="$TMP/notarize-$(basename "$TARGET").zip"
  ditto -c -k --keepParent "$TARGET" "$SUBMIT"
fi

echo "== 提交公证 $(basename "$TARGET")"
RESULT="$(xcrun notarytool submit "$SUBMIT" "${ARGS[@]}" --wait 2>&1)" || true
echo "$RESULT"
if ! echo "$RESULT" | grep -q "status: Accepted"; then
  ID="$(echo "$RESULT" | grep -m1 "id:" | awk '{print $2}')"
  [ -n "$ID" ] && xcrun notarytool log "$ID" "${ARGS[@]}" || true
  echo "::error::公证没有通过（$(basename "$TARGET")），原因见上面的日志"
  exit 1
fi

# stapler 要去 Apple 的 CloudKit 取票据，CI 上偶尔会 “The network connection was lost”（公证本身已通过），重试几次。
for ATTEMPT in 1 2 3 4 5 6; do
  if xcrun stapler staple "$TARGET"; then break; fi
  [ "$ATTEMPT" = 6 ] && { echo "::error::stapler 重试 6 次仍失败"; exit 1; }
  echo "stapler 第 $ATTEMPT 次失败（多半是网络抖动），20 秒后重试"
  sleep 20
done
xcrun stapler validate "$TARGET"
rm -f "$TMP/AuthKey.p8" "$TMP/notarize-"*.zip
