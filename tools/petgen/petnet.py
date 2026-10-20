"""
petgen 脚本共用的 HTTPS 工具。

python.org 安装的 Python 不带 CA 证书，默认证书库是空的，HTTPS 一律握手失败。
/etc/ssl/cert.pem 也不够：里面没有 ISRG Root X2，api.routin.ai 的 Let's Encrypt 新证书链验证不过。
所以证书库为空时，从 macOS 钥匙串导出系统根证书（和 curl、Safari 信任的一致）。证书校验始终开着。
"""
import shutil
import ssl
import subprocess
import urllib.request

SYSTEM_ROOTS = "/System/Library/Keychains/SystemRootCertificates.keychain"


def _context() -> ssl.SSLContext:
    ctx = ssl.create_default_context()
    if not ctx.cert_store_stats()["x509_ca"]:
        pem = subprocess.run(["security", "find-certificate", "-a", "-p", SYSTEM_ROOTS],
                             capture_output=True, text=True, check=True).stdout
        ctx.load_verify_locations(cadata=pem)
    return ctx


CONTEXT = _context()


def urlopen(req, timeout: float):
    return urllib.request.urlopen(req, timeout=timeout, context=CONTEXT)


def download(url: str, path) -> None:
    with urlopen(url, 300) as resp, open(path, "wb") as f:
        shutil.copyfileobj(resp, f)
