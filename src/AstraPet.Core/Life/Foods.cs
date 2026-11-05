namespace AstraPet.Core;

/// <summary>
/// 一种内置食物（design/pet-life/03 §2.3）。<see cref="Snack"/> 为真的是零食：不限量的喂不了，只能用完成番茄钟攒下来的；
/// 其余是主食 / 饮品：不限量，有冷却。图标用 emoji，三个平台都有，不用另外分发图片。
/// </summary>
public sealed record Food(string Id, string Name, string Icon, double Satiety, double Energy, double Mood, bool Snack, TimeSpan Cooldown);

public static class Foods
{
    public static readonly IReadOnlyList<Food> All =
    [
        new("rice-ball", "饭团", "🍙", 25, 0, 0, false, TimeSpan.FromMinutes(30)),
        new("milk", "牛奶", "🥛", 10, 5, 0, false, TimeSpan.FromMinutes(15)),
        new("vegetable", "青菜", "🥬", 20, 0, 0, false, TimeSpan.FromMinutes(30)),
        new("fish", "小鱼干", "🐟", 15, 0, 0, true, TimeSpan.Zero),
        new("cake", "小蛋糕", "🍰", 15, 0, 5, true, TimeSpan.Zero),
        new("ice-cream", "冰淇淋", "🍦", 10, 0, 8, true, TimeSpan.Zero),
        new("cookie", "饼干", "🍪", 10, 0, 0, true, TimeSpan.Zero),
    ];

    public static Food? Find(string id) => All.FirstOrDefault(f => f.Id == id);

    public static bool Exists(string id) => Find(id) is not null;

    public static IEnumerable<Food> Snacks => All.Where(f => f.Snack);
}
