namespace AstraPet.Core;

/// <summary>
/// 内置规则，等价于 0.1.4 写死在各处的反应（02-triggers.md §3.2）。不写进设置文件，升级时可以改；
/// 用户规则同 id 覆盖，enabled: false 可以关掉。
/// </summary>
public static class DefaultTriggers
{
    public static IReadOnlyList<TriggerRule> All => Create();

    private static List<TriggerRule> Create() =>
    [
        // 先说点击台词，再播动作：动作自己带台词时后者覆盖前者。
        new() { Id = "click-bubble", On = PetEvents.Clicked, Do = TriggerRule.None, Say = [TriggerRule.ClickLinesToken],
                Priority = "user", When = new() { Setting = nameof(PetSettings.ClickBubble) } },
        new() { Id = "click-react", On = PetEvents.Clicked, Do = "role:" + Roles.Click, Priority = "user",
                When = new() { Setting = nameof(PetSettings.ClickReact) } },
        new() { Id = "petted", On = PetEvents.Petted, Do = "role:" + Roles.Click, Say = [TriggerRule.PettingLinesToken],
                Priority = "user" },
        new() { Id = "greet", On = PetEvents.AppStarted, Do = "role:" + Roles.Greet, Priority = "system",
                When = new() { Setting = nameof(PetSettings.DailyGreeting) } },
        new() { Id = "pomodoro-end", On = PetEvents.FocusEnded, Do = "role:" + Roles.Celebrate, Priority = "system" },
        new() { Id = "pomodoro-rest-end", On = PetEvents.RestEnded, Do = "role:" + Roles.Click, Priority = "system" },
        new() { Id = "drink", On = PetEvents.Drink, Do = "role:" + Roles.Drink, Priority = "system" },
        new() { Id = "sit", On = PetEvents.Sit, Do = "role:" + Roles.Stretch, Priority = "system" },
        // 听歌期间一直唱由 Director 看设置决定（MusicPlaying），这条只负责「立刻开始唱」。
        new() { Id = "music", On = PetEvents.MusicStarted, Do = "role:" + Roles.Sing, Priority = "system",
                When = new() { PowerSaving = false } },
        new() { Id = "think", On = PetEvents.ChatThinking, Do = "role:" + Roles.Think, Priority = "agent",
                Until = [PetEvents.ChatAction, PetEvents.ChatReplied, PetEvents.ChatFailed] },
        new() { Id = "agent-action", On = PetEvents.ChatAction, Do = TriggerRule.EventActionToken, Priority = "agent" },
        new() { Id = "reply", On = PetEvents.ChatReplied, Do = "role:" + Roles.Click, Priority = "agent",
                When = new() { Event = new() { ["acted"] = "false" } } },
        // 外部 Agent（本地接口关着时收不到这些事件）。思考最多循环 10 分钟（Director.MaxEventLoop）。
        new() { Id = "external-started", On = PetEvents.ExternalStarted, Do = "role:" + Roles.Think, Priority = "agent",
                Until = [PetEvents.ExternalDone, PetEvents.ExternalFailed] },
        new() { Id = "external-done", On = PetEvents.ExternalDone, Do = "role:" + Roles.Celebrate, Priority = "agent" },
        new() { Id = "external-failed", On = PetEvents.ExternalFailed, Do = "role:" + Roles.Sad, Priority = "agent" },
        // 绑定了 hook 的编程智能体（design/agent-hooks §4.3）：按会话聚合过，只在最后一个会话停下时收起思考；
        // 庆祝、招手打断思考循环后会回到循环（还有别的会话在干活时）。冒泡由 AgentSessions 说，这里只管动作。
        new() { Id = "agent-working", On = PetEvents.AgentWorking, Do = "role:" + Roles.Think, Priority = "agent",
                Until = [PetEvents.AgentIdle] },
        new() { Id = "agent-done", On = PetEvents.AgentDone, Do = "role:" + Roles.Celebrate, Priority = "agent" },
        new() { Id = "agent-failed", On = PetEvents.AgentFailed, Do = "role:" + Roles.Sad, Priority = "agent" },
        new() { Id = "agent-waiting", On = PetEvents.AgentWaiting, Do = "role:" + Roles.Greet, Priority = "agent" },

        // ───── 宠物体系（design/pet-life/06 §3.2） ─────
        new() { Id = "feed-liked", On = PetEvents.Fed, Do = "role:" + Roles.Eat, Priority = "user", Speak = false,
                Say = ["哇！是{name}！最喜欢了", "{name}！你怎么知道我想吃这个", "嗷呜～{name}最棒了"],
                When = new() { Event = new() { ["liked"] = "liked" } } },
        new() { Id = "feed", On = PetEvents.Fed, Do = "role:" + Roles.Eat, Priority = "user", Speak = false,
                Say = ["谢谢～好好吃", "嗯嗯，{name}真香", "吧唧吧唧…"],
                When = new() { Event = new() { ["liked"] = "normal" } } },
        new() { Id = "feed-disliked", On = PetEvents.Fed, Do = "role:" + Roles.Sad, Priority = "user", Speak = false,
                Say = ["呜…不喜欢{name}", "{name}…好吧，我吃就是了"],
                When = new() { Event = new() { ["liked"] = "disliked" } } },
        new() { Id = "feed-full", On = PetEvents.FeedRefused, Do = "role:" + Roles.Confused, Priority = "user", Speak = false,
                Say = ["吃不下啦～", "肚子已经圆滚滚了"], When = new() { Event = new() { ["reason"] = "full" } } },
        new() { Id = "hungry", On = PetEvents.Hungry, Do = "role:" + Roles.Hungry, Priority = "ambient", Speak = false, Cooldown = 1800,
                Say = ["有点饿了…", "肚子咕咕叫了", "想吃点东西～"], When = new() { Setting = "NeedBubbles" } },
        new() { Id = "starving", On = PetEvents.Starving, Do = "role:" + Roles.Hungry, Priority = "ambient", Speak = false,
                Say = ["好饿好饿…", "能给我点吃的吗"], When = new() { Setting = "NeedBubbles" } },
        new() { Id = "satisfied", On = PetEvents.Satisfied, Do = "role:" + Roles.Happy, Priority = "ambient", Speak = false,
                Say = ["吃饱啦～"] },
        new() { Id = "sleepy", On = PetEvents.Sleepy, Do = "role:" + Roles.Yawn, Priority = "ambient", Speak = false,
                Say = ["好困…", "哈——欠"], When = new() { Setting = "NeedBubbles" } },
        new() { Id = "sleepy-night", On = PetEvents.Sleepy, Do = "role:" + Roles.Sleep, Priority = "system",
                When = new() { Hours = [22, 6] } },
        new() { Id = "rested", On = PetEvents.Rested, Do = TriggerRule.StancePrefix + Stances.Ground, Priority = "system", Speak = false,
                Say = ["睡饱啦！"], When = new() { Sleeping = true } },
        new() { Id = "level-up", On = PetEvents.LevelUp, Do = "role:" + Roles.Celebrate, Priority = "system", Speak = false,
                Say = [TriggerRule.BondLinesToken] },
        new() { Id = "snack", On = PetEvents.SnackEarned, Do = TriggerRule.None, Speak = false,
                Say = ["专注奖励：{icon}{name}，攒着喂我吧！"] },
        new() { Id = "returned", On = PetEvents.Returned, Do = "role:" + Roles.Greet, Priority = "system", Speak = false,
                Say = ["好久不见！想你了", "你终于回来啦～"] },
        new() { Id = "spammed", On = PetEvents.Spammed, Do = "role:" + Roles.Dizzy, Priority = "user", Speak = false,
                Say = ["别戳啦！头都晕了", "呜哇，晕了晕了"] },
        new() { Id = "thrown", On = PetEvents.Thrown, Do = TriggerRule.None, Speak = false, Chance = 0.6,
                Say = ["哇啊啊——", "飞起来啦！"] },
        new() { Id = "landed-hard", On = PetEvents.Landed, Do = "role:" + Roles.Dizzy, Priority = "system",
                When = new() { Event = new() { ["impact"] = "hard" } } },
        new() { Id = "landed", On = PetEvents.Landed, Do = "role:" + Roles.Land, Priority = "system",
                When = new() { Event = new() { ["impact"] = "soft" } } },
        new() { Id = "grabbed-wall", On = PetEvents.GrabbedWall, Do = TriggerRule.None, Speak = false, Chance = 0.5,
                Say = ["抓住了！"] },
        new() { Id = "climb-up", On = PetEvents.WallReached, Do = TriggerRule.StancePrefix + Stances.Wall, Priority = "ambient",
                Chance = 0.3, Cooldown = 300, When = new() { Setting = nameof(PetSettings.AllowClimb), Stance = [Stances.Ground] } },
        new() { Id = "look-at", On = PetEvents.CursorBehind, Do = "role:" + Roles.Turn, Priority = "ambient", Cooldown = 20,
                When = new() { Stance = [Stances.Ground] } },
        new() { Id = "woken", On = PetEvents.Woken, Do = TriggerRule.StancePrefix + Stances.Ground, Priority = "user", Speak = false,
                Say = ["唔…干嘛", "被吵醒了…"] },
        new() { Id = "sleep-request", On = PetEvents.SleepRequested, Do = "role:" + Roles.Sleep, Priority = "user" },
        new() { Id = "play-started", On = PetEvents.PlayStarted, Do = "role:" + Roles.Happy, Priority = "user", Speak = false,
                Say = ["来玩吧！", "抓我呀～"] },
        new() { Id = "play-caught", On = PetEvents.PlayCaught, Do = "role:" + Roles.Pounce, Priority = "user", Speak = false,
                Say = ["抓到啦！", "嘿！"] },
        new() { Id = "summon", On = PetEvents.Summoned, Do = "role:" + Roles.Greet, Priority = "user", Speak = false,
                Say = ["我来啦！", "叫我吗？", "嗖——到！"] },
        new() { Id = "play-ended", On = PetEvents.PlayEnded, Do = TriggerRule.None, Speak = false, Say = ["玩累了～"] },
    ];
}
