# Astra Pet 素材许可 / Pet Assets License

**版权所有 © 2026 RoutinAI (<https://routin.ai>)。保留所有权利。**
**Copyright © 2026 RoutinAI (<https://routin.ai>). All rights reserved.**

> **一句话 / In short**
> Astra Pet 的**源代码**以 MIT 协议开源（见 [LICENSE](LICENSE)）。**宠物形象和相关素材的版权归 RoutinAI 所有**，不在 MIT 授权范围内，只能按本文件的条款使用。
> The **source code** of Astra Pet is open source under the MIT license ([LICENSE](LICENSE)). The **pet characters and related assets are copyrighted by RoutinAI** and are NOT covered by the MIT license; they may only be used under the terms of this document.

---

## 1. 什么是「宠物素材」 / What are the "Pet Assets"

下列内容，以及由它们派生的一切（截取、缩放、转码、转成别的格式、逐帧图片、预览图、重新上色等）：

1. `src/Assets/videos/` ——内置宠物（小t 和她脚边的水豚）的全部动画和配置；
2. `characters/小t/` 整个目录——角色设定图、立绘、首尾帧、原始生成视频、处理后的视频、预览图，以及 `character.json` 里的角色设定和提示词；
3. `astra/` ——角色设定图；
4. 官方发布包里随程序分发的上述内容，以及 RoutinAI 日后新增的官方宠物角色。

The following, and anything derived from them (crops, rescaling, transcoding, conversion to other formats, frame sequences, previews, recolouring, etc.):

1. `src/Assets/videos/` — all animations and configuration of the built-in pet (Xiao T and her capybara);
2. the whole `characters/小t/` directory — character sheets, standing figure, first/last frames, raw generated videos, processed videos, previews, and the character settings and prompts in `character.json`;
3. `astra/` — the character sheet;
4. the above as shipped in official release packages, and any official pet characters RoutinAI adds later.

## 2. 不在本文件范围内的内容 / What this document does NOT cover

- **程序源码**：MIT，见 [LICENSE](LICENSE)。 / The program source code: MIT, see [LICENSE](LICENSE).
- **第三方库和数据**：保留各自的许可，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)；品牌图标里的商标归各自所有者。 / Third-party libraries and data keep their own licenses, see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md); trademarks shown in brand icons belong to their owners.
- **`characters/deepseek/`**：不是 RoutinAI 的作品，来自第三方项目 dsh-pet（<https://github.com/PC2005-cloud/dsh-pet>），版权属于其原作者，使用请遵守原作者的授权。 / **`characters/deepseek/`** is not a work of RoutinAI; it comes from the third-party project dsh-pet and belongs to its original authors. Follow their terms.
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

代码是 MIT 的，你可以自由 fork、修改、发布你自己的版本。**发布时请把内置宠物换成你自己的角色**：把 `src/Assets/videos/` 换成你自己的素材，并删除 `characters/小t/` 和 `astra/`。程序不依赖这些素材运行，角色目录的格式见 `characters/小t/README.md`。

The code is MIT: you are free to fork, modify and publish your own version. **When you publish, replace the built-in pet with your own character**: swap `src/Assets/videos/` for your own assets and remove `characters/小t/` and `astra/`. The program does not depend on these assets; see `characters/小t/README.md` for the character directory format.

## 6. 免责和终止 / Disclaimer and termination

宠物素材按「现状」提供，不附带任何明示或暗示的担保。违反本文件的条款，本许可自动终止。

The Pet Assets are provided "as is", without warranty of any kind. This license terminates automatically if you breach these terms.
