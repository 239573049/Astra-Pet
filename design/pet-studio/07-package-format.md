# 07 角色包格式 2（.astrapet）

**【待定 D13】** 推荐沿用 `.astrapet` 扩展名，清单里 `format` 从 1 升到 2。理由：用户已经认识这个扩展名，
`PetPack.Inspect` 本来就会对「格式太新」给出「请升级 Astra Pet」的提示，老版本遇到格式 2 的包能友好地拒绝。
不另起扩展名，也不另做「工程包」格式——需要再编辑时用「附带源文件」（§3）。

## 1. 目录

```
pet.json                     清单（必需）
clips/<动作 id>/*.webm        片段，640×360 VP9 yuva420p
preview.png                  预览图（可选，建议）
icon.png                     256×256 头像（可选，控制台列表和托盘用）
LICENSE.txt                  许可全文（可选）
source/                      源文件（可选，见 §3）
  sheet.png                  全方位设定图
  figure.png                 透明底站姿
  first_frame.png            首尾帧（含抠像色）
  keyframes/*.png            分段动作的关键帧
  studio.json                角色设定卡、公共前缀、每个动作的提示词和生成参数（不含密钥、不含提供商地址）
```

打包规则沿用 `pack_pet.py`：webm 和 png 用 `ZIP_STORED`，json 用 deflate；条目按名字排序、时间戳固定，同样的输入打出逐字节相同的包。

## 2. pet.json

```jsonc
{
  "format": 2,
  "id": "shiba",                       // [a-z0-9-]，覆盖层按它关联（01 §4）
  "name": "柴柴",
  "version": "1.0.0",                  // 包自己的版本，作者升级包时递增；控制台显示
  "default": false,                    // 只有内置包用
  "author": "token",
  "license": "CC BY-NC-SA 4.0",
  "licenseUrl": "https://creativecommons.org/licenses/by-nc-sa/4.0/",
  "homepage": "",
  "credits": ["用 Astra Pet 工作台制作；视频由 Seedance 生成"],

  "minAppVersion": "0.2.0",            // 需要的最低程序版本（用到新角色、新字段时设）

  "render": {                          // 和 PetClip / 处理管线的常量一致；现在只有这一种，留着以后扩展
    "width": 640, "height": 360, "bodyHeight": 270, "feetY": 330
  },

  "persona": {                         // 03 §3.3，全部可选，每字段 ≤200 字
    "name": "柴柴",
    "identity": "橘色短毛、戴红色针织围巾的圆滚滚柴犬",
    "personality": "懒洋洋但很黏人",
    "speech": "说话慢吞吞，句尾爱加「汪」",
    "likes": ["晒太阳", "肉干"],
    "catchphrases": [],
    "greeting": "汪～以后我就住在这里啦"
  },

  "actions": [                         // 01 §3
    {
      "id": "idle-breathe", "name": "待机呼吸", "description": "安静地站着呼吸，偶尔眨眼",
      "roles": ["idle"], "clips": ["clips/idle-breathe/a.webm"]
    },
    {
      "id": "wag", "name": "摇尾巴转圈", "description": "开心地摇着尾巴原地转一圈",
      "roles": ["click", "happy", "greet"], "clips": ["clips/wag/a.webm"],
      "lines": ["汪！"]
    },
    {
      "id": "trot", "name": "小跑", "description": "侧身原地小跑",
      "roles": ["move"], "clips": ["clips/trot/a.webm"],
      "gait": { "stride": 220, "curve": [0, 0, 0.01, "…"] }
    }
  ],

  "triggers": [                        // 可选：包自带的触发规则，只能引用本包的动作和内置事件（02 §3）
    { "id": "pet-wag", "on": "pet.petted", "do": "wag" }
  ]
}
```

## 3. 源文件（可选）

导出时勾选「附带源文件」才有。用途：

- 以后给这只宠物生成新动作（需要 `first_frame.png` 和公共前缀）；
- 「以此为基础新建」工作台工程（D2 方案 C）；
- 别人拿到包也能在自己的工作台里继续做（许可允许的前提下）。

`studio.json` 里 **不写** 提供商地址、密钥、账本、原始 mp4。体积一般 2～5 MB。

## 4. 校验与安全

`PetPack` 的解压检查扩展为：

| 检查 | 规则 |
|---|---|
| 文件白名单 | `pet.json`、`preview.png`、`icon.png`、`LICENSE.txt`、`clips/<id>/*.webm`、`source/*.png`、`source/keyframes/*.png`、`source/studio.json`；格式 1 的 `videos/...` 继续认 |
| 路径 | 不能有 `\`、绝对路径、`.`、`..`、空段（现有逻辑） |
| 动作 id / 目录名 | `[a-z0-9-]{1,32}` |
| 引用完整性 | `actions[].clips` 必须都在包里；包里不能有没被引用的 webm（防夹带） |
| 大小 | 解压后总大小上限从 512 MB 提到 1 GB（动作多了以后会超）；单个 webm ≤ 64 MB；png ≤ 16 MB |
| 文本长度 | `persona` 各字段截断；`description` ≤ 60 字；`lines` 每条 ≤ 60 字、最多 20 条 |
| 触发规则 | 只能引用本包动作 id 或 `role:`；只能用内置事件；不能有 `setting` 条件（不能读用户设置） |
| 角色 | `idle`、`drag`、`click`、`move`、`turn` 每个至少有一个动作（D14：维持五类必需），和格式 1 的要求一致 |

包里没有任何可执行内容：没有脚本、没有 URL 回调、没有会被程序「执行」的字段。人设文本按 03 §3.3 处理。

## 5. 兼容

| 读 \ 包 | 格式 1 | 格式 2 |
|---|---|---|
| 新程序 | 按 01 §5 合成动作；没有人设，用通用人设 | 完整支持 |
| 老程序（≤0.1.4） | 支持 | `Inspect` 报「角色包格式 2 太新，请升级 Astra Pet」，不会崩 |

- `pack_pet.py` 增加 `--format 2`，读角色目录里的 `character.json` 生成 `actions`（id 用 `--id-map` 或拼音 / 序号），
  这样小t 和大肥鱼的内置包可以升级到格式 2 并补上人设和动作描述。
- 内置包升级时，覆盖层按包 id 关联、动作 id 要和格式 1 合成时的 id（`click-1` 这种）保持一致，或在 `pet.json` 里给一个 `legacyIds` 映射，
  以免用户在 0.2.x 早期对内置角色做的调整失效。

## 6. 用户角色文件夹

用户角色目录里的文件夹（不打包）也支持格式 2：文件夹里放 `pet.json` + `clips/`，和包内结构一致。没有 `pet.json` 时按格式 1 处理（现在的 `character.json` + `videos/`）。
工作台「安装到我的角色」写的就是打好的 `.astrapet` 文件，不是文件夹，避免两种形态并存。
