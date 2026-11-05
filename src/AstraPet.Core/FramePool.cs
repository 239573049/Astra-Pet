using System.Collections.Concurrent;

namespace AstraPet.Core;

/// <summary>
/// 帧缓冲池。所有片段同一尺寸（<see cref="PetClip.FrameBytes"/>，约 900KB），跨片段复用，
/// 免得每帧在大对象堆上新分配。解码线程借，界面画完还；线程安全。
/// 平时在用的帧：两条流（当前段 + 预取的下一段）各 3 帧排队、1 帧在读，加上屏幕上的 1 帧，约 9 帧。
/// 池子留 12 帧够周转；多出来的（比如工作台同时预览几段）还给 GC。
/// 缓冲分配在固定对象堆（POH）：不参与压缩、不在大对象堆上留碎片，拷进位图时也不用再固定。
/// </summary>
public static class FramePool
{
    private const int Limit = 12;

    private static readonly ConcurrentBag<byte[]> Pool = new();

    public static byte[] Rent() =>
        Pool.TryTake(out var frame) ? frame : GC.AllocateUninitializedArray<byte>(PetClip.FrameBytes, pinned: true);

    /// <summary>用完的帧还回来。池满了就丢给 GC。</summary>
    public static void Return(byte[] frame)
    {
        if (frame.Length == PetClip.FrameBytes && Pool.Count < Limit)
            Pool.Add(frame);
    }
}
