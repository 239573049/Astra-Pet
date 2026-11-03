using AstraPet.Core.Behaviors;
using Avalonia.Threading;

namespace AstraPet.Adapters;

/// <summary>
/// Core 的行为层用它回到 UI 线程。用 Background 优先级，和以前 DispatcherTimer 的回调一样：
/// 不抢输入和渲染，宠物动画不会因为提醒、检测而卡一下。
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static readonly AvaloniaUiDispatcher Instance = new();

    private AvaloniaUiDispatcher() { }

    public void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
}
