namespace AstraPet.Core;

/// <summary>宠物体系的默认台词。角色包的动作台词（lines）优先；这里是规则里的兜底。</summary>
public static class LifeLines
{
    /// <summary>升到某一级时说的话（02 §5）。</summary>
    public static IReadOnlyList<string> LevelUp(int level) => level switch
    {
        2 => ["我们认识啦～以后请多关照！"],
        3 => ["和你待在一起越来越自在了"],
        4 => ["我们是朋友了吧？嘿嘿"],
        5 => ["你是我的好朋友！"],
        6 => ["好像越来越离不开你了"],
        7 => ["每天都想见到你"],
        8 => ["你是我最好的朋友"],
        9 => ["我们就像家人一样"],
        >= 10 => ["你是我最重要的人！"],
        _ => ["嘿嘿"],
    };
}
