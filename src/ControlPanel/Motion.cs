using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace AstraPet.ControlPanel;

/// <summary>
/// 控制台的动画都走合成线程（Composition）：动画由渲染线程按时钟推进，不触发布局，
/// 动画开始后 UI 线程卡一下（比如某一页第一次布局）也不会让动画丢帧、末尾突然一跳。
/// 对应的是 Avalonia 样式动画和 Transitions，它们在 UI 线程上按墙钟时间走，UI 线程一卡就跳。
/// 统一的节奏：进场 320 到 420ms，先快后慢；同一屏的几块依次错开 45ms。
/// </summary>
internal static class Motion
{
    public static readonly Easing OutCubic = new CubicEaseOut();
    public static readonly Easing OutQuint = new QuinticEaseOut();

    /// <summary>
    /// 进场：从透明淡入到不透明，同时从偏移位置回到原位（<paramref name="dx"/>、<paramref name="dy"/> 是起点相对终点的偏移），
    /// <paramref name="scaleFrom"/> 小于 1 时再从这个比例放大到原样（以控件中心为轴）。
    /// </summary>
    public static void Enter(Visual visual, double dx, double dy, TimeSpan duration, TimeSpan delay = default,
        double scaleFrom = 1, Easing? easing = null)
    {
        if (ElementComposition.GetElementVisual(visual) is not { } cv) return;
        easing ??= OutCubic;

        var fade = cv.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, easing);
        Configure(fade, duration, delay);
        cv.StartAnimation("Opacity", fade);

        if (dx != 0 || dy != 0) Slide(visual, dx, dy, duration, delay, easing);

        if (scaleFrom < 1)
        {
            cv.CenterPoint = new Vector3D(visual.Bounds.Width / 2, visual.Bounds.Height / 2, 0);
            var scale = cv.Compositor.CreateVector3DKeyFrameAnimation();
            scale.InsertKeyFrame(0f, new Vector3D(scaleFrom, scaleFrom, 1));
            scale.InsertKeyFrame(1f, new Vector3D(1, 1, 1), easing);
            Configure(scale, duration, delay);
            cv.StartAnimation("Scale", scale);
        }
    }

    /// <summary>只做位移：布局已经把控件放到了终点，这里让它从偏移位置滑回来（导航高亮胶囊用）。</summary>
    public static void Slide(Visual visual, double dx, double dy, TimeSpan duration, TimeSpan delay = default, Easing? easing = null)
    {
        if (ElementComposition.GetElementVisual(visual) is not { } cv) return;
        var slide = cv.Compositor.CreateVector3DKeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3D(dx, dy, 0));
        slide.InsertKeyFrame(1f, default, easing ?? OutCubic);
        Configure(slide, duration, delay);
        cv.StartAnimation("Translation", slide);
    }

    private static void Configure(KeyFrameAnimation animation, TimeSpan duration, TimeSpan delay)
    {
        animation.Duration = duration;
        animation.DelayTime = delay;
        // 错开的几块在轮到自己之前就得先是透明的，否则会先闪一下完整的样子。
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
    }
}
