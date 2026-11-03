using System.Collections.Concurrent;

namespace AstraPet.Core;

/// <summary>
/// 帧缓冲池。所有片段同一尺寸（<see cref="PetClip.FrameBytes"/>，约 900KB），跨片段复用，
/// 免得每帧在大对象堆上新分配。解码线程借，界面画完还；线程安全。
/// </summary>
public static class FramePool
{
    private const int Limit = 24;

    private static readonly ConcurrentBag<byte[]> Pool = new();

    public static byte[] Rent() => Pool.TryTake(out var frame) ? frame : new byte[PetClip.FrameBytes];

    /// <summary>用完的帧还回来。池满了就丢给 GC。</summary>
    public static void Return(byte[] frame)
    {
        if (frame.Length == PetClip.FrameBytes && Pool.Count < Limit)
            Pool.Add(frame);
    }
}
