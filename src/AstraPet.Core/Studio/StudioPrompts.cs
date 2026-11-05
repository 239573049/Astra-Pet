using System.Globalization;
using System.Text;

namespace AstraPet.Core.Studio;

/// <summary>一段动作模板：按时间轴写，时间用占总时长的比例，按所选时长缩放成「0~3秒，…」。</summary>
public sealed record ActionTemplate(
    string Id, string Name, string Description, IReadOnlyList<string> Roles, bool Required, int Duration,
    IReadOnlyList<(double End, string Text)> Timeline, IReadOnlyList<string> Lines)
{
    /// <summary>在哪个姿态下播（design/pet-life/05 §1 ④）；过渡动作是起点姿态。</summary>
    public string Stance { get; init; } = Stances.Ground;
    /// <summary>过渡动作的终点姿态；null 表示不是过渡（首尾帧是同一张）。</summary>
    public string? To { get; init; }
    /// <summary>步态沿 Y 轴（墙上攀爬）。</summary>
    public bool Vertical { get; init; }

    /// <summary>「0~3秒，……；3~7秒，……」。最后一段总是收在总时长。</summary>
    public string Render(int duration)
    {
        var sb = new StringBuilder();
        var start = 0;
        for (var i = 0; i < Timeline.Count; i++)
        {
            var end = i == Timeline.Count - 1 ? duration : Math.Clamp((int)Math.Round(Timeline[i].End * duration), start + 1, duration - 1);
            sb.Append($"{start}~{end}秒，{Timeline[i].Text}");
            sb.Append(i == Timeline.Count - 1 ? "" : "；");
            start = end;
        }
        return sb.ToString();
    }

    public PlannedAction Plan() => new()
    {
        Id = Id, Name = Name, Description = Description, Roles = Roles.ToList(), Duration = Duration,
        Prompt = Render(Duration), Template = Id, Lines = Lines.ToList(),
        Mirror = true, Stance = Stance, To = To, Vertical = Vertical,
    };
}

/// <summary>
/// 工作台的提示词模板（04 §3）。来自小t 的 character.json 和踩过的坑：首尾帧同图、纯色抠像底、镜头固定、原地动作、结束回到初始姿势。
/// </summary>
public static class StudioPrompts
{
    public static readonly IReadOnlyList<string> Styles =
        ["Q 版 2.5D 动漫", "日系动漫", "3D 手办风", "像素风", "手绘水彩"];

    public static readonly IReadOnlyList<string> Resolutions = ["480p", "720p", "1080p"];

    public static readonly IReadOnlyList<string> KeyColors = ["green", "blue", "magenta"];

    public static string KeyName(string key) => key switch { "blue" => "蓝色", "magenta" => "品红色", _ => "绿色" };
    public static string KeyHex(string key) => key switch { "blue" => "#0000FF", "magenta" => "#FF00FF", _ => "#00FF00" };
    public static (byte R, byte G, byte B) KeyRgb(string key) => key switch
    {
        "blue" => (0, 0, 255),
        "magenta" => (255, 0, 255),
        _ => (0, 255, 0),
    };

    private const string Back = "回到与第一帧完全一致的初始姿势";

    public static IReadOnlyList<ActionTemplate> Templates { get; } =
    [
        new("idle", "待机呼吸", "安静地站着呼吸，偶尔眨眼", [Core.Roles.Idle], true, 10,
            [(0.3, "角色随呼吸极轻微地起伏，手臂自然放松"), (0.7, "眨两次眼，头轻轻歪一下再回正"), (1, "平稳" + Back + "。动作幅度很小，适合无缝循环")], []),
        new("drag", "被拎起悬空", "被拎起来，慌张地踢腿", [Core.Roles.Drag], true, 6,
            [(0.25, "角色像被看不见的手从后领轻轻拎起，双脚离开地面一点点"), (0.8, "悬在半空，慌张地小幅踢腿、挥动手臂"), (1, "轻轻落回原位站好，" + Back + "。身体不要拉长变形")], []),
        new("wave", "开心挥手", "开心地朝你挥手打招呼", [Core.Roles.Click, Core.Roles.Happy, Core.Roles.Greet], true, 8,
            [(0.25, "眼睛一亮，开心地笑起来"), (0.75, "举起一只手朝镜头挥手打招呼，身体随之左右轻摆，头顶冒出两三个不透明的粉色小爱心"), (1, "爱心消失，放下手，" + Back)],
            ["嗨～", "你好呀！"]),
        new("trot", "原地小跑", "侧身朝左原地小跑", [Core.Roles.Move], true, 10,
            [(0.1, "角色转成侧身，面朝画面左侧"), (0.9, "保持侧身朝左，原地小跑：双腿交替抬起落下、手臂前后摆动，身体轻微上下起伏，但整体不在画面里移动"), (1, "转回正面，" + Back)], []),
        new("look", "东张西望", "好奇地左右张望", [Core.Roles.Turn], true, 8,
            [(0.35, "好奇地把头和上身转向画面左侧张望"), (0.7, "转向画面右侧张望"), (1, "歪头想了想，转回正面，" + Back + "。双脚不离开原位")], []),
        new("shy", "害羞", "脸红，捂着脸扭来扭去", [Core.Roles.Click, Core.Roles.Shy], false, 8,
            [(0.25, "脸颊一下子变红"), (0.75, "双手捂住脸颊，害羞地左右轻轻扭动身体，脸旁冒出几个不透明的小爱心"), (1, "爱心消失，放下双手，" + Back)], ["才、才没有呢"]),
        new("jump", "庆祝跳起", "开心地原地跳起来庆祝", [Core.Roles.Celebrate, Core.Roles.Happy], false, 6,
            [(0.3, "握拳蓄力，表情兴奋"), (0.75, "原地高高跳起，双手举过头顶欢呼，周围冒出几颗不透明的彩色星星"), (1, "落回原位，星星消失，" + Back)], ["耶！"]),
        new("stretch", "伸懒腰", "伸一个大大的懒腰", [Core.Roles.Stretch], false, 8,
            [(0.3, "打个哈欠"), (0.75, "双手举过头顶，伸一个大大的懒腰，身体微微后仰"), (1, "放下双手，" + Back)], []),
        new("drink", "喝水", "拿起一杯水喝几口", [Core.Roles.Drink], false, 8,
            [(0.25, "手里出现一个不透明的卡通水杯"), (0.75, "端起水杯喝了几口，满足地眯起眼"), (1, "水杯消失，放下手，" + Back)], []),
        new("confused", "歪头疑惑", "歪着头，头顶冒问号", [Core.Roles.Confused], false, 6,
            [(0.3, "眨眨眼，露出疑惑的表情"), (0.75, "歪着头，头顶冒出一个不透明的问号"), (1, "问号消失，头回正，" + Back)], ["嗯？"]),
        new("sad", "难过低头", "难过地低下头", [Core.Roles.Sad], false, 8,
            [(0.3, "表情慢慢变得失落"), (0.75, "低下头，肩膀垂下，轻轻叹气"), (1, "抬起头打起精神，" + Back)], []),
        new("sway", "跟着音乐摇摆", "跟着音乐左右摇摆", [Core.Roles.Sing], false, 10,
            [(0.15, "听到音乐，眼睛一亮"), (0.85, "跟着节奏左右摇摆身体、轻轻点头，头顶飘出几个不透明的音符"), (1, "音符消失，" + Back)], []),
    ];

    /// <summary>
    /// 宠物体系的模板（design/pet-life/05 §1 ④）：地面上的吃、饿、打哈欠、晕、下落、落地、扑；
    /// 墙组（贴墙待机、攀爬、上墙、下墙、松手）；坐和睡。墙组和坐 / 睡要先在 ③ 生成对应的关键帧。
    /// </summary>
    public static IReadOnlyList<ActionTemplate> LifeTemplates { get; } =
    [
        new("eat", "吃东西", "双手捧着小碗吃东西", [Core.Roles.Eat], true, 8,
            [(0.2, "双手捧起一个不透明的卡通小碗（碗里看不清是什么）"), (0.8, "低头吃了几口，嚼一嚼，满足地眯起眼睛"), (1, "小碗消失，" + Back)], []),
        new("hungry", "饿了", "摸着肚子，可怜巴巴", [Core.Roles.Hungry], true, 6,
            [(0.3, "摸摸肚子，肚子瘪下去一点"), (0.75, "可怜巴巴地看向镜头，头顶冒出一个不透明的小饭团图标"), (1, "图标消失，" + Back)], []),
        new("yawn", "打哈欠", "张大嘴打哈欠、揉眼睛", [Core.Roles.Yawn], true, 6,
            [(0.5, "张大嘴打一个大大的哈欠"), (0.8, "揉揉眼睛"), (1, Back)], []),
        new("dizzy", "晕乎乎", "摇摇晃晃，头顶转星星", [Core.Roles.Dizzy], true, 6,
            [(0.7, "身体摇摇晃晃，眼睛变成转圈的样子，头顶有几颗不透明的星星在转"), (1, "甩甩头清醒过来，星星消失，" + Back)], []),
        new("fall", "下落", "手脚张开慌张挣扎（循环）", [Core.Roles.Fall], true, 4,
            [(1, "像在空中下落一样，手脚张开慌张地挥动，衣服和头发向上飘。整体不在画面里移动，首尾姿势一致，适合无缝循环")], []),
        new("land", "落地", "膝盖弯曲缓冲后站直", [Core.Roles.Land], true, 4,
            [(0.4, "像刚从高处落地，膝盖弯曲缓冲、身体下沉"), (1, "站直身体，" + Back)], []),
        new("pounce", "扑", "原地向前一扑", [Core.Roles.Pounce], false, 4,
            [(0.3, "压低身体，盯住前方"), (0.7, "原地朝前一扑，双手向前抓"), (1, Back)], ["抓到啦！"]),
        new("wall-idle", "贴墙待机", "抓着墙轻轻呼吸、回头看你", [Core.Roles.Idle], true, 8,
            [(0.4, "手脚抓着左侧看不见的墙面，随呼吸轻微起伏"), (0.75, "回头看了一眼镜头，眨眨眼"), (1, "转回去，" + Back + "。手脚始终贴着墙面同一位置")], [])
            { Stance = Stances.Wall },
        new("climb", "攀爬", "手脚交替往上爬", [Core.Roles.Climb], true, 6,
            [(1, "手脚交替做向上攀爬的动作，一个完整的手脚循环。身体整体不在画面里上移，手脚接触墙面的竖线不变，首尾姿势一致")], [])
            { Stance = Stances.Wall, Vertical = true },
        new("wall-enter", "上墙", "走近左边，跳起扒住墙", [], true, 4,
            [(0.4, "转身面向画面左侧"), (0.8, "轻轻一跳，双手双脚扒到左侧看不见的竖直墙面上"), (1, "稳稳贴在墙上，姿势和尾帧一致")], [])
            { Stance = Stances.Ground, To = Stances.Wall },
        new("wall-exit", "下墙", "松开墙轻轻跳下站好", [], true, 4,
            [(0.5, "松开墙面，轻轻跳下来"), (1, "落地转回正面站好，姿势和尾帧一致")], [])
            { Stance = Stances.Wall, To = Stances.Ground },
        new("let-go", "松手", "手一滑松开墙", [], true, 4,
            [(0.6, "手一滑，惊讶地「哇」一声"), (1, "双手松开墙面，回到贴墙的姿势（之后由程序让它掉下去）")], [])
            { Stance = Stances.Wall, To = Stances.Air },
        new("sit-enter", "坐下", "原地坐下", [], false, 4,
            [(1, "原地慢慢坐下，姿势和尾帧一致")], []) { Stance = Stances.Ground, To = Stances.Sit },
        new("sit-idle", "坐着待机", "坐着晃晃脚", [Core.Roles.Idle], false, 8,
            [(0.6, "坐着轻轻晃脚，随呼吸起伏"), (1, Back)], []) { Stance = Stances.Sit },
        new("sit-exit", "起身", "站起来", [], false, 4,
            [(1, "从坐姿站起来，回到正面站姿")], []) { Stance = Stances.Sit, To = Stances.Ground },
        new("sleep-enter", "入睡", "坐着打瞌睡，慢慢躺下", [Core.Roles.Sleep], false, 6,
            [(0.5, "打瞌睡，头一点一点"), (1, "慢慢趴下 / 蜷起来睡着，姿势和尾帧一致")], []) { Stance = Stances.Sit, To = Stances.Lie },
        new("sleep-loop", "睡着", "安静地睡觉，冒 Z", [Core.Roles.Idle, Core.Roles.Sleep], false, 8,
            [(1, "安静地睡着，随呼吸轻微起伏，头顶偶尔冒出不透明的「Z」，结束前消失。首尾姿势一致")], []) { Stance = Stances.Lie },
        new("sleep-exit", "醒来", "伸懒腰醒来站好", [], false, 6,
            [(0.5, "醒来，揉揉眼睛坐起"), (1, "站起来伸个懒腰，回到正面站姿")], []) { Stance = Stances.Lie, To = Stances.Ground },
        new("ceiling-idle", "倒挂待机", "倒挂在天花板上晃晃", [Core.Roles.Idle], false, 8,
            [(0.6, "手脚抓着画面顶部看不见的天花板，身体倒挂，轻轻晃动"), (1, "朝镜头眨眨眼，" + Back + "。手脚始终贴着天花板同一高度")], [])
            { Stance = Stances.Ceiling },
        new("ceiling-crawl", "倒挂爬行", "倒挂着朝左爬", [Core.Roles.Climb], false, 6,
            [(1, "倒挂着做朝画面左侧爬行的手脚交替动作，一个完整循环。身体整体不在画面里移动，贴着天花板的高度不变，首尾姿势一致")], [])
            { Stance = Stances.Ceiling },
        new("ceiling-enter", "翻上天花板", "从墙顶翻上去倒挂", [], false, 4,
            [(0.6, "从贴墙的姿势往上伸手"), (1, "翻身倒挂到天花板上，姿势和尾帧一致")], []) { Stance = Stances.Wall, To = Stances.Ceiling },
        new("ceiling-exit", "回到墙上", "从天花板翻回墙上", [], false, 4,
            [(1, "从倒挂的姿势翻回贴墙的姿势，姿势和尾帧一致")], []) { Stance = Stances.Ceiling, To = Stances.Wall },
        new("ceiling-drop", "从天花板松手", "手一松掉下去", [], false, 4,
            [(0.6, "手一滑，惊讶地张大嘴"), (1, "松开天花板，回到倒挂的姿势（之后由程序让它掉下去）")], []) { Stance = Stances.Ceiling, To = Stances.Air },
    ];

    /// <summary>所有模板：基础 + 宠物体系。</summary>
    public static IEnumerable<ActionTemplate> AllTemplates => Templates.Concat(LifeTemplates);

    public static ActionTemplate? Template(string id) => AllTemplates.FirstOrDefault(t => t.Id == id);

    /// <summary>视频时长下限：Seedance 2.x 只接受 4～15 秒，3 秒的请求会被直接拒掉（HTTP 400）。</summary>
    public const int MinDuration = 4;

    /// <summary>需要关键帧的姿态（③ 选装）和显示名。</summary>
    public static readonly IReadOnlyList<(string Stance, string Name)> KeyframeStances =
        [(Stances.Wall, "贴墙"), (Stances.Sit, "坐"), (Stances.Lie, "睡"), (Stances.Ceiling, "倒挂")];

    public static string StanceName(string stance) => stance switch
    {
        Stances.Ground => "地面",
        Stances.Air => "空中",
        _ => KeyframeStances.FirstOrDefault(k => k.Stance == stance).Name ?? stance,
    };

    /// <summary>③ 选装姿态的关键帧：以站姿为参考做图片编辑，纯色抠像底（05 §1 ③）。</summary>
    public static string Keyframe(ProjectData p, string stance)
    {
        var key = KeyName(p.KeyColor);
        var pose = stance switch
        {
            Stances.Wall => "侧身贴在画面左侧一道看不见的竖直墙面上：双手双脚抓住墙面，身体竖直，脸朝左（朝墙）略微偏向观众。" +
                            "墙是看不见的、和背景同色，不要画出墙、柱子或任何支撑物；手脚接触墙面的位置在同一条竖线上",
            Stances.Sit => "原地坐在地上，双腿自然伸出或盘起，双手放在身前，看向正前方",
            Stances.Lie => "趴在地上蜷起来睡着，闭着眼",
            Stances.Ceiling => "倒挂在画面顶部一道看不见的天花板上：双手双脚抓住上方的平面，身体倒过来朝下，脸朝向观众。" +
                               "天花板是看不见的、和背景同色，不要画出来；手脚接触天花板的位置在同一条横线上",
            _ => "正面站立",
        };
        return $"参考图1是这个角色的标准站姿。请画同一个角色的另一个姿势：{pose}。" +
               $"外貌、服装、配色、比例与参考图完全一致{Colon(Appearance(p))}。画风：{p.Style}。只画这一个角色。" +
               $"完整全身都在画面内，四周留白。背景是纯{key} {KeyHex(p.KeyColor)}，均匀没有渐变；不要地面、阴影、文字或其他物体。";
    }

    /// <summary>
    /// 动作的公共前缀：地面上首尾同图的用工程的前缀（用户可改）；墙上的、过渡的（首尾帧不同）用程序拼的前缀。
    /// </summary>
    public static string PrefixFor(ProjectData p, PlannedAction a)
    {
        if (a.Stance == Stances.Ground && a.To is null) return p.Prefix;
        var key = KeyName(p.KeyColor);
        var appearance = Appearance(p) is { Length: > 0 } ap ? ap : "这个角色";
        var common = $"1. 背景始终是纯{key} {KeyHex(p.KeyColor)}，没有阴影、地面或其他物体；" +
                     "2. 镜头固定，全程全身远景，不推拉摇移，不切特写；" +
                     $"3. 角色{Proportion(p)}，比例、外貌和服装始终与首帧一致；" +
                     "4. 画面里只有这个角色，不出现手、手指或其他人物；" +
                     "5. 特效和道具画成不透明的实心卡通样式，结束前完全消失；";
        if (a.To is { } to && to != Stances.Air)
            return $"桌面宠物过渡动作视频。首帧是{appearance}的{StanceName(a.Stance)}姿势，尾帧是同一角色的{StanceName(to)}姿势，都在纯{key}背景前。要求：" +
                   common + "6. 角色从首帧姿势自然过渡到尾帧姿势，位移尽量小，最后一秒与尾帧完全一致。动作：";
        var wall = a.Stance == Stances.Wall;
        var ceiling = a.Stance == Stances.Ceiling;
        var where = wall ? "贴在画面左侧一道看不见的竖直墙面上" : ceiling ? "倒挂在画面顶部一道看不见的天花板上" : "的" + StanceName(a.Stance) + "姿势";
        return $"桌面宠物循环动作视频。首帧和尾帧是同一张图：{appearance}{where}，纯{key}背景。要求：" +
               common + (wall ? "6. 手脚与墙的接触点始终在同一条竖线上，不离开墙面，墙不要画出来；"
                   : ceiling ? "6. 手脚与天花板的接触点始终在同一条横线上，不离开天花板，天花板不要画出来；"
                   : "6. 角色原地动作，不在画面里移动；") +
               "7. 最后一秒回到与第一帧完全一致的姿势和位置。动作：";
    }

    /// <summary>自定义动作在 <see cref="PlannedAction.Template"/> 里的标记：提示词按动作描述生成。</summary>
    public const string CustomTemplate = "custom";

    /// <summary>
    /// 用户自己描述的动作：一句话描述放在时间轴中段，前后补上「准备」和「回到第一帧」，
    /// 道具、特效按踩过的坑要求不透明、结束前消失。
    /// </summary>
    public static ActionTemplate Custom(string name, string description, IReadOnlyList<string> roles, int duration)
    {
        var what = description.Trim().ReplaceLineEndings("，").TrimEnd('。', '，', '；', '.', ' ');
        if (what.Length == 0) what = name.Trim();
        return new(CustomTemplate, name.Trim(), description.Trim(), roles, false, duration,
            [(0.15, "角色看向镜头，准备开始，表情自然"),
             (0.85, what + "。动作在原地完成，出现的道具和特效画成不透明的实心卡通样式"),
             (1, "道具和特效消失，" + Back + "。双脚回到原位")], []);
    }

    /// <summary>触发时机（语义角色）的中文名，给 ④ 的自定义动作选。顺序就是界面上的顺序。</summary>
    public static readonly IReadOnlyList<(string Role, string Name)> RoleNames =
    [
        (Core.Roles.Click, "被点击"), (Core.Roles.Happy, "开心"), (Core.Roles.Greet, "打招呼"), (Core.Roles.Celebrate, "庆祝"),
        (Core.Roles.Affection, "撒娇"), (Core.Roles.Shy, "害羞"), (Core.Roles.Surprised, "惊讶"), (Core.Roles.Confused, "疑惑"),
        (Core.Roles.Sad, "难过"), (Core.Roles.Angry, "生气"), (Core.Roles.Sing, "听歌"), (Core.Roles.Think, "思考"),
        (Core.Roles.Talk, "说话"), (Core.Roles.Sleep, "睡觉"), (Core.Roles.Drink, "喝水"), (Core.Roles.Stretch, "伸懒腰"),
        (Core.Roles.Idle, "待机"), (Core.Roles.Turn, "转身"),
    ];

    /// <summary>
    /// 基础套装：工作台里必需的动作——原来的五类（待机、拖拽、点击、走路、转身），加上宠物体系的「地面常用」
    /// （吃东西、饿了、打哈欠、晕乎乎、下落、落地）和「墙组」（贴墙待机、攀爬、上墙、下墙、松手）。
    /// 只管工作台：缺了不能确认规划、不能导出；程序加载角色包时仍只要求原来的五类，老角色包照样能用（design/pet-life/05 §1 ④）。
    /// </summary>
    public static IEnumerable<PlannedAction> BaseKit() => RequiredTemplates.Select(t => t.Plan());

    public static IEnumerable<ActionTemplate> RequiredTemplates => AllTemplates.Where(t => t.Required);

    /// <summary>外貌为空（只给了参考图、没写描述）时省掉「：……」这半句。</summary>
    private static string Colon(string appearance) => appearance.Length > 0 ? "：" + appearance : "";

    private static string Lead(string appearance) => appearance.Length > 0 ? appearance + "，" : "角色";

    /// <summary>一句话外观：设定卡里的，没有就用用户的描述。</summary>
    public static string Appearance(ProjectData p) =>
        (p.Card.Appearance.Trim() is { Length: > 0 } a ? a : p.Description.Trim()).ReplaceLineEndings("，");

    private static string Proportion(ProjectData p) => p.HeadRatio > 0
        ? $"Q 版约 {p.HeadRatio.ToString("0.#", CultureInfo.InvariantCulture)} 头身"
        : "保持参考图里的体型比例";

    /// <summary>④ 的公共前缀（04 §3 ④）。</summary>
    public static string Prefix(ProjectData p)
    {
        var key = KeyName(p.KeyColor);
        var companion = p.Companion.Trim().Length > 0 ? $"和它的伙伴（{p.Companion.Trim()}）" : "";
        return $"桌面宠物循环动作视频。首帧和尾帧是同一张图：{Lead(Appearance(p))}正面站在纯{key}背景前{(companion.Length > 0 ? "，身边是" + companion[1..] : "")}。要求：" +
               $"1. 背景始终是纯{key} {KeyHex(p.KeyColor)}，没有阴影、地面或其他物体；" +
               "2. 镜头固定，全程全身远景，不推拉摇移，不切特写；" +
               "3. 角色原地动作，不在画面里移动，跳起后落回原位；" +
               $"4. 角色{Proportion(p)}，比例、外貌和服装始终与首帧一致；" +
               $"5. 画面里只有这个角色{companion}，不出现手、手指或其他人物；" +
               "6. 特效和道具画成不透明的实心卡通样式，不要半透明或发光，结束前完全消失；" +
               "7. 最后一秒回到与第一帧完全一致的姿势和位置。动作：";
    }

    /// <summary>② 设定图：同一角色的多视图设定稿，纯白背景。</summary>
    public static string Sheet(ProjectData p, bool hasReferences)
    {
        var sb = new StringBuilder();
        sb.Append(hasReferences
            ? "参考图是这个角色的形象。请照参考图画一张这个角色的角色设定稿（三视图）："
            : "请为下面这个原创角色画一张角色设定稿（三视图）：");
        sb.Append($"{Appearance(p)}。");
        if (p.Companion.Trim().Length > 0) sb.Append($"它身边有一个常驻的小伙伴：{p.Companion.Trim()}。");
        sb.Append($"画风：{p.Style}，{Proportion(p)}。");
        sb.Append("同一个角色的正面、四分之三侧面、正侧面、背面全身像并排，比例和服装细节完全一致；");
        sb.Append("纯白背景，不要文字、色卡、标注、边框、地面或阴影。");
        return sb.ToString();
    }

    /// <summary>③ 标准站姿：参考设定图，纯色抠像底。</summary>
    public static string Figure(ProjectData p)
    {
        var key = KeyName(p.KeyColor);
        var sb = new StringBuilder();
        sb.Append("参考图1是角色设定图。请画这个角色的一张高清全身正面标准站姿立绘，用作桌面宠物动画的起始帧。");
        sb.Append($"比例：{Proportion(p)}，与设定图一致。");
        sb.Append("姿势：正面直立，双脚并拢站稳，双臂自然下垂、稍微离开身体，闭嘴微笑，眼睛看向正前方。");
        sb.Append($"外貌、服装和配色与设定图完全一致{Colon(Appearance(p))}。画风：{p.Style}。");
        if (p.Companion.Trim().Length > 0) sb.Append($"伙伴（{p.Companion.Trim()}）站在角色脚边，脚底在同一条地面线上，不要挡住角色。");
        else sb.Append("只画这一个角色。");
        sb.Append($"构图：完整全身从头顶到脚底都在画面内，角色居中，四周留出空白。背景是纯{key} {KeyHex(p.KeyColor)}，均匀没有渐变；");
        sb.Append("不要地面、阴影、文字、色卡或任何其他物体，角色身上不要用和背景相同的颜色。");
        return sb.ToString();
    }
}
