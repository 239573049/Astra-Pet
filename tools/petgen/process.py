"""
把 Seedance 生成的绿幕 mp4 处理成桌宠素材包。
  tools/petgen/.venv/bin/python tools/petgen/process.py characters/小t [--force] [--only 名字 ...]

读 raw/<分类>/<名字>.mp4，写 videos/<分类>/<名字>.webm，再写 videos/ 下的 move_strides.json、
text_clips.json。videos/ 就是桌宠程序的素材目录（ASTRA_PET_VIDEOS 指向它）。

每段做四件事：
1. 抠绿。前景和纯绿按覆盖率 a 混合时，「绿比红蓝多出的量」约等于 1 - a，据此算 alpha。
   实心像素把绿色压到不超过红蓝里较大的那个；半透明的边缘像素改用附近实心像素的颜色，
   这样边缘既不带绿，也不会因为压掉绿色而发暗。
2. 统一大小。量前 5 帧站姿的包围盒，缩放到身高 270、脚底 y=330、水平居中，
   和原版素材包的 body_box [212, 60, 428, 330] 对齐，窗口里看起来一样大。
3. 编码成 640x360、libvpx-vp9、yuva420p，帧率沿用源视频。
4. 走路片段：walk_window 时间段之外曲线走平；时间段之内，按腿部轮廓的帧间变化累加，
   脚不动的帧曲线也走平。步幅用 character.json 里的 stride，需要看实际效果再调。

最后打印质量报告：首尾帧是否一致（循环会不会跳）、人物有没有在画面里漂移、背景有没有抠干净。
"""
import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

FFMPEG = "/opt/homebrew/bin/ffmpeg"
OUT_W, OUT_H = 640, 360
BODY_H, FEET_Y, CENTER_X = 270, 330, 320
# alpha = 1 - 绿色超出量，再拉一下色阶：压缩噪声带来的少量绿色超出不算前景，接近 1 的直接实心。
ALPHA_LO, ALPHA_HI = 0.12, 0.85
CRF = 32
EDGE_RADIUS = 2  # 边缘取色时，实心区颜色往外扩散的半径（源视频像素）


def probe(path: Path) -> tuple[int, int, float]:
    log = subprocess.run([FFMPEG, "-hide_banner", "-i", str(path)], capture_output=True, text=True).stderr
    video = re.search(r"Stream #0:\d+.*?: Video: (.*)", log)
    if not video:
        sys.exit(f"没有视频流: {path}")
    w, h = map(int, re.search(r"\b(\d{2,5})x(\d{2,5})\b", video.group(1)).groups())
    fps = re.search(r"([0-9.]+) fps", video.group(1))
    return w, h, float(fps.group(1)) if fps else 24.0


def read_frames(path: Path, w: int, h: int, limit: int | None = None):
    args = [FFMPEG, "-v", "error", "-i", str(path)]
    if limit:
        args += ["-frames:v", str(limit)]
    proc = subprocess.Popen(args + ["-f", "rawvideo", "-pix_fmt", "rgb24", "-"], stdout=subprocess.PIPE)
    size = w * h * 3
    while (buf := proc.stdout.read(size)) and len(buf) == size:
        yield np.frombuffer(buf, np.uint8).reshape(h, w, 3)
    proc.stdout.close()
    proc.wait()


def key(rgb: np.ndarray) -> np.ndarray:
    """绿幕帧 → RGBA（uint8），边缘不带绿也不发暗。"""
    f = rgb.astype(np.float32) / 255
    r, g, b = f[..., 0], f[..., 1], f[..., 2]
    rb = np.maximum(r, b)
    excess = np.clip(g - rb, 0, 1)
    # Seedance 画的绿幕不一定是纯 #00FF00，有的片段偏暗（绿色超出量只有 0.86 左右）。
    # 按这一帧背景实际的超出量归一化，否则整个背景会残留几个百分点的不透明度，看起来像一层雾。
    sample = excess[::4, ::4]
    bg = sample[sample > 0.4]
    if bg.size > sample.size * 0.2:
        excess = np.clip(excess / np.median(bg), 0, 1)
    alpha = np.clip(((1 - excess) - ALPHA_LO) / (ALPHA_HI - ALPHA_LO), 0, 1)
    rgb_out = np.stack([r, np.minimum(g, rb), b], axis=-1)  # 去绿：绿色不超过红蓝的较大值

    # 半透明的边缘像素是前景和绿幕混出来的，只压绿色会让边缘发暗、偏青。
    # 改用附近实心像素的颜色：把实心区的颜色往外扩散几个像素，透明度仍用抠像算出来的。
    solid = alpha >= 0.95
    edge = (alpha > 0) & ~solid
    if edge.any() and solid.any():
        mask = Image.fromarray((solid * 255).astype(np.uint8), "L").filter(ImageFilter.GaussianBlur(EDGE_RADIUS))
        color = Image.fromarray((rgb_out * solid[..., None] * 255).astype(np.uint8), "RGB").filter(
            ImageFilter.GaussianBlur(EDGE_RADIUS))
        m = np.asarray(mask, np.float32)[..., None] / 255
        spread = np.asarray(color, np.float32) / 255 / np.maximum(m, 1e-3)
        use = edge & (m[..., 0] > 0.02)
        rgb_out[use] = np.clip(spread[use], 0, 1)

        # 附近没有实心像素的半透明区域（淡入淡出的爱心、光点）借不到颜色。
        # 按「观测色 = a·前景 + (1-a)·绿幕」反解前景色，只压绿色会让它们发暗发灰。
        lone = edge & ~use
        if lone.any():
            bg_mask = excess >= 0.95
            key_rgb = f[bg_mask].mean(axis=0) if bg_mask.any() else np.array([0.0, 1.0, 0.0], np.float32)
            a_lin = np.maximum(1 - excess[lone], 0.05)[:, None]
            fg = (f[lone] - (1 - a_lin) * key_rgb) / a_lin
            fg[:, 1] = np.minimum(fg[:, 1], np.maximum(fg[:, 0], fg[:, 2]))
            rgb_out[lone] = np.clip(fg, 0, 1)

    out = np.concatenate([rgb_out, alpha[..., None]], axis=-1)
    out[..., :3] *= (alpha > 0)[..., None]  # 全透明处颜色清零，免得 4:2:0 色度采样把颜色渗到边上
    return (out * 255 + 0.5).astype(np.uint8)


def standing_box(path: Path, w: int, h: int) -> tuple[int, int, float]:
    """前 5 帧站姿的头顶、脚底和水平中心（源视频坐标）。"""
    tops, bottoms, centers = [], [], []
    for rgb in read_frames(path, w, h, limit=5):
        a = key(rgb)[..., 3] > 128
        ys, xs = np.nonzero(a)
        tops.append(ys.min())
        bottoms.append(ys.max())
        centers.append(np.median(xs))
    return int(np.median(tops)), int(np.median(bottoms)), float(np.median(centers))


def place(rgba: np.ndarray, scale: float, dx: int, dy: int) -> np.ndarray:
    """缩放后贴到 640x360 透明画布上。用预乘模式缩放，边缘不会发灰发黑。"""
    im = Image.fromarray(rgba, "RGBA").convert("RGBa")
    im = im.resize((round(im.width * scale), round(im.height * scale)), Image.LANCZOS).convert("RGBA")
    canvas = Image.new("RGBA", (OUT_W, OUT_H), (0, 0, 0, 0))
    canvas.paste(im, (dx, dy))
    return np.asarray(canvas)


def gait_curve(leg_motion: list[float], fps: float, window: tuple[float, float]) -> list[float]:
    """腿部轮廓帧间变化 → 圈内累计进度 0..1。窗口外和腿不动的帧不推进。"""
    n = len(leg_motion)
    lo, hi = int(window[0] * fps), min(n, int(window[1] * fps))
    step = np.zeros(n)
    inside = np.asarray(leg_motion[lo:hi])
    if inside.size:
        floor = 0.2 * np.median(inside)
        step[lo:hi] = np.where(inside > floor, inside, 0)
    if step.sum() == 0:
        step[lo:hi] = 1
    curve = np.cumsum(step) / step.sum()
    curve[0] = 0
    return [round(float(v), 4) for v in curve]


def process(src: Path, dst: Path, action: dict) -> dict:
    w, h, fps = probe(src)
    top, bottom, cx = standing_box(src, w, h)
    scale = BODY_H / (bottom - top + 1)
    dx, dy = round(CENTER_X - cx * scale), round(FEET_Y - bottom * scale)

    dst.parent.mkdir(parents=True, exist_ok=True)
    enc = subprocess.Popen([FFMPEG, "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgba",
                            "-s", f"{OUT_W}x{OUT_H}", "-r", f"{fps:g}", "-i", "-",
                            "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-crf", str(CRF), "-b:v", "0",
                            "-row-mt", "1", "-an", str(dst)], stdin=subprocess.PIPE)

    legs = (slice(FEET_Y - int(0.35 * BODY_H), FEET_Y + 1), slice(CENTER_X - BODY_H // 2, CENTER_X + BODY_H // 2))
    first = prev = None
    leg_motion, drift_x, drift_feet, edge_leak, haze = [], 0.0, 0, 0.0, 0.0
    for rgb in read_frames(src, w, h):
        out = place(key(rgb), scale, dx, dy)
        enc.stdin.write(out.tobytes())
        a = out[..., 3].astype(np.float32) / 255
        if first is None:
            first = out
            x0 = np.average(np.arange(OUT_W), weights=a.sum(0)) if a.sum() else CENTER_X
        solid = a > 0.5
        if solid.any():
            ys, xs = np.nonzero(solid)
            drift_x = max(drift_x, abs(float(np.average(np.arange(OUT_W), weights=a.sum(0))) - x0))
            drift_feet = max(drift_feet, abs(int(ys.max()) - FEET_Y))
        edge_leak = max(edge_leak, float((a[:, :80] > 0.1).mean() + (a[:, -80:] > 0.1).mean()) / 2)
        haze = max(haze, float(((a > 0) & (a < 0.1)).mean()))  # 正常只有抗锯齿边缘是这种几乎透明的像素
        leg_motion.append(0.0 if prev is None else float(np.abs(a[legs] - prev[legs]).mean()))
        prev = a
    enc.stdin.close()
    if enc.wait() != 0:
        sys.exit(f"编码失败: {dst}")

    last_a, first_a = prev > 0.5, first[..., 3] > 128
    iou = float((last_a & first_a).sum() / max((last_a | first_a).sum(), 1))
    report = {"frames": len(leg_motion), "fps": fps, "loop_iou": iou, "drift_x": drift_x,
              "drift_feet": drift_feet, "edge_leak": edge_leak, "haze": haze, "src": f"{w}x{h}", "scale": scale}
    if action["category"] == "move":
        report["curve"] = gait_curve(leg_motion, fps, tuple(action.get("walk_window", (0, len(leg_motion) / fps))))
    return report


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("char_dir", type=Path)
    p.add_argument("--force", action="store_true", help="已处理过的也重新处理")
    p.add_argument("--only", nargs="*")
    args = p.parse_args()

    char_dir = args.char_dir
    cfg = json.loads((char_dir / "character.json").read_text(encoding="utf-8"))
    videos = char_dir / "videos"
    strides_file = videos / "move_strides.json"
    strides = json.loads(strides_file.read_text(encoding="utf-8")) if strides_file.exists() else {}

    print(f"{'片段':<16}{'帧':>5}{'fps':>5}  {'首尾IoU':>7}  {'横向漂移':>6}  {'脚底偏移':>6}  {'边缘残留':>6}  {'背景雾':>6}")
    warnings = []
    for action in cfg["actions"]:
        if args.only and action["name"] not in args.only:
            continue
        src = char_dir / "raw" / action["category"] / f"{action['name']}.mp4"
        dst = videos / action["category"] / f"{action['name']}.webm"
        if not src.exists():
            print(f"{action['name']:<16}还没生成")
            continue
        if dst.exists() and dst.stat().st_mtime > src.stat().st_mtime and not args.force:
            print(f"{action['name']:<16}已处理，跳过（--force 重做）")
            continue
        r = process(src, dst, action)
        print(f"{action['name']:<16}{r['frames']:>5}{r['fps']:>5.0f}  {r['loop_iou']:>7.3f}  "
              f"{r['drift_x']:>5.1f}px  {r['drift_feet']:>5d}px  {r['edge_leak']:>6.2%}  {r['haze']:>6.2%}")
        if r["loop_iou"] < 0.9:
            warnings.append(f"{action['name']}：首尾帧轮廓重合度 {r['loop_iou']:.2f}，循环衔接时会跳")
        if action["category"] != "move" and r["drift_x"] > 25:
            warnings.append(f"{action['name']}：人物在画面里横向漂了 {r['drift_x']:.0f}px")
        if r["haze"] > 0.05:
            warnings.append(f"{action['name']}：{r['haze']:.0%} 的像素几乎透明但不为零，背景没抠干净，会像一层雾")
        if r["edge_leak"] > 0.01:
            warnings.append(f"{action['name']}：画面两侧有 {r['edge_leak']:.1%} 的非透明像素，可能背景没抠干净或有道具出界")
        if "curve" in r:
            strides[action["name"]] = {"stride": action.get("stride", 200), "curve": r["curve"]}

    strides["_comment"] = ("移动动画每圈地面位移（px，640x360 素材坐标）。curve 是 process.py 按腿部轮廓帧间变化"
                           "在 walk_window 内累加得到的估算，stride 来自 character.json，需按实际效果调整。")
    videos.mkdir(parents=True, exist_ok=True)
    strides_file.write_text(json.dumps(strides, ensure_ascii=False, indent=1), encoding="utf-8")
    no_mirror = [a["name"] for a in cfg["actions"] if a.get("no_mirror")]
    (videos / "text_clips.json").write_text(json.dumps({"no_mirror": no_mirror}, ensure_ascii=False, indent=2),
                                            encoding="utf-8")

    missing = [c for c in ("idle", "click", "drag", "move", "turn") if not any((videos / c).glob("*.webm"))]
    if missing:
        warnings.append(f"这些分类还没有片段，桌宠启动会报错：{', '.join(missing)}")
    for w in warnings:
        print("注意：" + w)


if __name__ == "__main__":
    main()
