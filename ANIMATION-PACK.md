# 动画资源包

程序逻辑与动画分开。替换素材后无需重新编译。收到新动画时，可将 GIF 直接导入某个动作，或先把 WebP、APNG、视频转换为透明 PNG 序列再导入。转换后的 PNG 请保持相同画布尺寸、角色大小和脚底基线。

## 直接在程序里替换

1. 右键桌宠 → 设置 → 桌宠设置与动画。
2. 选择待机、向右走、向左走、挥手、跳跃、休息、被拖动或开心。
3. 点击“替换所选动作”，选择一个 GIF 或一组 PNG。PNG 按文件名中的数字自然排序，GIF 采用文件自带的帧时长。

导入会在程序数据目录创建一份新资源包，原包保留。也可以把图片拖到桌宠上，然后在设置窗口中选择要替换的动作。关闭再启动仍会使用导入后的资源。

## 资源包目录

```text
my-pet/
  pet.json
  frames/
    idle.png
    walk-00.png
    walk-01.png
  wave.gif
  sheet.png
```

`pet.json` 的最小示例：

```json
{
  "schemaVersion": 1,
  "name": "我的桌宠",
  "canvas": { "width": 200, "height": 220 },
  "animations": {
    "idle": { "file": "frames/idle.png", "defaultFrameDurationMs": 1000 },
    "walk-right": {
      "loop": true,
      "frames": [
        { "file": "frames/walk-00.png", "durationMs": 180 },
        { "file": "frames/walk-01.png", "durationMs": 180 }
      ]
    },
    "walk-left": { "mirrorOf": "walk-right" },
    "wave": { "file": "wave.gif", "loop": false }
  }
}
```

动作名：`idle` 必需；其余标准动作是 `walk-right`、`walk-left`、`wave`、`jump`、`sleep`、`drag`、`happy`。自定义动作可在设置窗口预览。缺少动作会使用待机；缺少单侧走路动画时可镜像另一侧。镜像保留帧的播放顺序。

`head-pat` 为摸头动作，使用 `frames/head-pat.gif`，12 帧、4860 毫秒、单次播放。源文件为 `shiro-head-pat-refit-20261028/final/touch.gif`，与用户给出的浅灰底预览动作、帧顺序和时长一致，但保留透明背景。点击头顶或头发触发。

`cheek-block` 为戳脸动作，使用 `frames/cheek-block.gif`，12 帧、4250 毫秒、单次播放。源文件为 `shiro-cheek-block-animation-20261028-v3/final/cheek-block.gif`，保留透明背景和原始帧时长。点击角色窗口宽度 30%–70%、高度 25%–41% 的脸部区域优先触发戳脸，其他头部位置摸头，身体聊天，拖动优先。已有自定义资源包会创建副本，仅补入新动作。

`thinking` 用于发送消息后等待及生成回复期间，循环播放。内置文件 `frames/thinking.gif` 保留用户提供的 6 帧和 4000 毫秒原始时长；可以在设置窗口选择“对话 · 思考”替换。两种聊天入口都使用它，结束、取消、错误或关闭生成窗口后恢复日常动作；拖拽优先，释放后若仍在生成则继续思考。升级时会为原有自定义资源包创建副本，只补入思考动作。

每个动作只能使用 `file`、`frames`、`sheet`、`mirrorOf` 中一种。

- `file`：静态 PNG/JPG/BMP 或动画 GIF。GIF 按逻辑画布、偏移、透明度、清除/恢复方式合成，时长取 GIF 元数据。
- `frames`：PNG 序列。每帧有 `file`、`durationMs`，还可设置画布单位的 `offsetX`、`offsetY`。默认资源用偏移实现占位跳跃；完整跳跃序列通常不需要偏移。
- `sheet`：规则精灵图，例子见下。
- `mirrorOf`：引用另一个动作，水平镜像；可以重复引用，但不能循环引用。

精灵图动作示例：

```json
{
  "loop": true,
  "defaultFrameDurationMs": 180,
  "sheet": {
    "file": "sheet.png",
    "frameWidth": 192,
    "frameHeight": 208,
    "columns": 8,
    "start": 8,
    "count": 8,
    "durationsMs": [180, 180, 180, 180, 180, 180, 180, 180]
  }
}
```

`start` 从 0 开始，按从左到右、从上到下数格子。以上例子可读取 8 列图集的第二行。无需不等间隔时可省略 `durationsMs`。

单帧时长为 20–60000 毫秒，一个动作最多 512 帧。图片路径须位于资源包内。单张图片边长最多 4096；GIF 画布边长最多 2048。建议桌宠成品使用约 200×220 的透明帧，减少内存占用。运行时保留比例，不拉伸角色。

程序会监听资源包文件变化，约半秒后重新加载。无效 JSON、缺失图片或错误精灵图范围不会替换已经加载成功的动画；改好后会再次尝试，也可手动“重新加载”。

GIF 合成采用微软公布的 [WIC GIF 元数据字段](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-native-image-format-metadata-queries)。框架检查用包含局部帧和多种 disposal 的 GIF 验证合成结果。


## 自动休息与哈欠动作

`yawn` 为单次哈欠动作；自动闲置触发只播放一遍，导入时也默认不循环。
`sleep`、`sleep-ear-twitch`、`sleep-tail-flick` 为休息的三个版本，每轮选取概率分别为 80%、10%、10%。内置版本均为 4 秒，每轮结束才重新抽取；自定义包若缺少细微动作，则回退到 `sleep`。
设置窗口可以分别预览与替换这四个动作。程序升级会将用户提供的素材接入已有自定义包的副本，之后用户仍可自行替换。


`knock` 为日常随机敲击屏幕动作，导入时默认单次播放。用户原始 GIF 的透明度和时长由运行时读取。程序会为已有自定义资源包创建副本，仅补入敲击动画，保留其他动作。

`foot-swing` 为随机坐着晃脚动作，原始 GIF 为 192×208、8 帧、2800 毫秒，单次播放。与 `knock` 共享随机 3–7 分钟间隔，等概率选择；聊天、设置、拖拽、休息时暂停触发，点击可打断。右键及动画设置均可预览。升级已有自定义包时创建副本，仅增加此动作。

`climb-left` 使用原始 `frames/crawl-forward-v7.gif`（480×320、9 帧、880 毫秒），循环播放；`climb-right` 镜像引用 `climb-left`。运行时分别旋转 90° / -90°并贴到屏幕工作区侧边，不改动 GIF 文件。进入侧边 12 像素范围时判定 50% 概率，触发后随机向上爬 80–240 像素，到顶会提前停下。一次靠近只判定一次，离开 28 像素以上或重新拖放后才重新判定。已有自定义资源包升级时创建副本补入左右动作，保留其他素材。
