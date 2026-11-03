# Astra Pet 素材许可 / Pet Assets License

**版权所有 © 2026 RoutinAI (<https://routin.ai>)。保留所有权利。**
**Copyright © 2026 RoutinAI (<https://routin.ai>). All rights reserved.**

> **一句话 / In short**
> Astra Pet 的**源代码**以 MIT 协议开源（见 [LICENSE](LICENSE)）。**宠物形象和相关素材的版权归 RoutinAI 所有**，不在 MIT 授权范围内，只能按本文件的条款使用。
> The **source code** of Astra Pet is open source under the MIT license ([LICENSE](LICENSE)). The **pet characters and related assets are copyrighted by RoutinAI** and are NOT covered by the MIT license; they may only be used under the terms of this document.

---

## 1. 什么是「宠物素材」 / What are the "Pet Assets"

下列内容，以及由它们派生的一切（截取、缩放、转码、转成别的格式、逐帧图片、预览图、重新上色等）：

1. `src/Assets/pets/xiaot.astrapet` ——默认内置宠物（小t 和她脚边的水豚）的角色包，包括其中的全部动画和配置；
2. `characters/小t/` 整个目录——角色设定图、立绘、首尾帧、原始生成视频、处理后的视频、预览图，以及 `character.json` 里的角色设定和提示词；
3. `astra/` ——角色设定图；
4. `src/Assets/icons/` ——应用图标（用角色头像做的，由 `tools/icon/make_icons.py` 生成）；
5. 官方发布包里随程序分发的上述内容，以及 RoutinAI 日后新增的官方宠物角色。

The following, and anything derived from them (crops, rescaling, transcoding, conversion to other formats, frame sequences, previews, recolouring, etc.):

1. `src/Assets/pets/xiaot.astrapet` — the character pack of the default built-in pet (Xiao T and her capybara), including all its animations and configuration;
2. the whole `characters/小t/` directory — character sheets, standing figure, first/last frames, raw generated videos, processed videos, previews, and the character settings and prompts in `character.json`;
3. `astra/` — the character sheet;
4. `src/Assets/icons/` — the app icons (made from the character's portrait by `tools/icon/make_icons.py`);
5. the above as shipped in official release packages, and any official pet characters RoutinAI adds later.

## 2. 不在本文件范围内的内容 / What this document does NOT cover

- **程序源码**：MIT，见 [LICENSE](LICENSE)。 / The program source code: MIT, see [LICENSE](LICENSE).
- **第三方库和数据**：保留各自的许可，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)；品牌图标里的商标归各自所有者。 / Third-party libraries and data keep their own licenses, see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md); trademarks shown in brand icons belong to their owners.
- **内置角色「大肥鱼」（`src/Assets/pets/dafeiyu.astrapet`，源素材目录 `characters/deepseek/` 不在仓库里）**：不是 RoutinAI 的作品，不适用本文件，按 **[CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/deed.zh-hans)**（署名 · 非商业性使用 · 相同方式共享）授权。形象原型「溟月」出自 B 站 [@上善无形](https://space.bilibili.com/4456176)，DeepSeek 版（女仆鲸鱼娘）形象设计出自 B 站 [@ZipZipPipe](https://space.bilibili.com/4168597)（[授权声明](https://www.bilibili.com/opus/1247156596114456581)），动画素材来自 [dsh-pet](https://github.com/PC2005-cloud/dsh-pet)。这是社区二创形象，**不是 DeepSeek 官方形象**，与 DeepSeek 没有关联。Astra Pet 选取了其中 11 段动画，转码为 640×360 带透明通道的 VP9 WebM，并新增了走路步幅和不镜像片段的配置。**不得用于任何商业目的**；再分发或改编时须保留署名并使用相同许可。完整说明在角色包里的 `LICENSE.txt`（也见 `tools/pets/dafeiyu-LICENSE.txt`）。
- **The built-in character "Dafeiyu" (`src/Assets/pets/dafeiyu.astrapet`; its source folder `characters/deepseek/` is not in the repository)** is not a work of RoutinAI and is not covered by this document. It is licensed under **[CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/)**. The original character "Mingyue" is by [@上善无形](https://space.bilibili.com/4456176) on Bilibili, the DeepSeek (maid whale girl) design is by [@ZipZipPipe](https://space.bilibili.com/4168597), and the animations come from [dsh-pet](https://github.com/PC2005-cloud/dsh-pet). It is a community fan character, **not an official DeepSeek character**, and is not affiliated with DeepSeek. Astra Pet selected 11 of its animations, transcoded them to 640×360 VP9 WebM with alpha, and added walk-stride and no-mirror configuration. **No commercial use of any kind**; redistributions and adaptations must keep the attribution and use the same license. Full notice: `LICENSE.txt` inside the pack (also `tools/pets/dafeiyu-LICENSE.txt`).
- **你自己用 `tools/petgen` 生成的宠物**：版权归生成者本人，并受你所用生成服务的条款约束，RoutinAI 对它们不主张权利。 / Pets that you generate yourself with `tools/petgen` belong to you and are subject to the terms of the generation service you use; RoutinAI claims no rights in them.

## 3. 你可以做什么 / What you may do

RoutinAI 授予你一项免费、非独占、不可转让的许可，你可以：

1. **使用**：运行 Astra Pet（官方发布版，或你用本仓库源码构建的版本），在其中使用宠物素材，供你自己使用（个人、学习、研究，也包括在工作电脑上自己使用）；
2. **查看和复刻**：在 GitHub 上查看、克隆、fork 本仓库（受 GitHub 服务条款约束）；
3. **引用**：为评测、报道、教程截图或录屏，并注明「宠物形象 © RoutinAI (routin.ai)」。

RoutinAI grants you a free, non-exclusive, non-transferable license to:

1. **Use**: run Astra Pet (an official release, or a build you made from this repository) and use the Pet Assets inside it, for your own use (personal, study, research, including on your own work computer);
2. **View and fork**: view, clone and fork this repository on GitHub (subject to GitHub's Terms of Service);
3. **Quote**: show them in reviews, news, tutorials, screenshots or screen recordings, with the credit "Pet artwork © RoutinAI (routin.ai)".

## 4. 需要 RoutinAI 书面许可的事 / What needs RoutinAI's written permission

未经 RoutinAI 事先书面许可，不得：

1. 把宠物素材拿出本程序单独使用——作为其他软件、网站、游戏、视频、表情包、壁纸、头像等的内容；
2. 修改、二次创作，或制作衍生角色、周边（包括抠出、重绘、换色后再发布）；
3. 商业使用——出售、授权、投放广告、做周边，或用在任何收费的产品或服务里；
4. 再分发宠物素材——包括把它们打进你自己的构建、安装包、镜像、压缩包后公开发布；
5. 用宠物素材训练或微调 AI 模型，或用它们生成相似角色并声称是原创或官方作品；
6. 暗示你或你的产品与 RoutinAI 有官方关系，或使用 RoutinAI 的名称和标识。

Without RoutinAI's prior written permission you may not:

1. use the Pet Assets outside this program — as content in other software, websites, games, videos, stickers, wallpapers, avatars, etc.;
2. modify them, create derivative works, or make derivative characters or merchandise (including cutting out, redrawing or recolouring them and publishing the result);
3. use them commercially — sell, license, advertise with them, make merchandise, or include them in any paid product or service;
4. redistribute the Pet Assets — including publishing them inside your own builds, installers, images or archives;
5. use them to train or fine-tune AI models, or use them to generate similar characters and claim these as original or official;
6. suggest that you or your product is affiliated with RoutinAI, or use RoutinAI's name and marks.

联系 / Contact: <https://routin.ai>　源码仓库 / Source repository: <https://github.com/239573049/Astra-Pet>

## 5. 想基于本项目做自己的版本 / Making your own version

代码是 MIT 的，你可以自由 fork、修改、发布你自己的版本。**发布时请把内置宠物换成你自己的角色**：把 `src/Assets/pets/xiaot.astrapet` 换成你自己的角色包（用 `tools/pack_pet.py` 打包，加 `--default`），把 `src/Assets/icons/` 里的图标换成你自己的，并删除 `characters/小t/` 和 `astra/`。程序不依赖这些素材运行，角色目录的格式见 `characters/小t/README.md`。大肥鱼按它自己的 CC BY-NC-SA 4.0 许可处理（见第 2 节）。

The code is MIT: you are free to fork, modify and publish your own version. **When you publish, replace the built-in pet with your own character**: replace `src/Assets/pets/xiaot.astrapet` with your own character pack (built with `tools/pack_pet.py --default`), swap the icons in `src/Assets/icons/` for your own and remove `characters/小t/` and `astra/`. The program does not depend on these assets; see `characters/小t/README.md` for the character directory format. Dafeiyu follows its own CC BY-NC-SA 4.0 license (see section 2).

## 6. 免责和终止 / Disclaimer and termination

宠物素材按「现状」提供，不附带任何明示或暗示的担保。违反本文件的条款，本许可自动终止。

The Pet Assets are provided "as is", without warranty of any kind. This license terminates automatically if you breach these terms.
