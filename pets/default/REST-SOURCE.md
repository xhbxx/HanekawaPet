# 团睡与打哈欠素材

用户提供的 `E:/pet-animation/silver-cat-rest/curled-rest-v1/团睡休息-三个版本.zip`。
直接复制其中 `frames/rest-still`、`frames/rest-ear-twitch`、`frames/rest-tail-flick` 的 18 个透明 PNG，不重绘、不缩放，均为 192×208。
时长采用 `final/animations.json`，每个版本均为 4000 毫秒：

- 静息 `sleep`：700、600、650、650、600、800 毫秒。
- 动耳朵 `sleep-ear-twitch`：1600、400、120、120、300、1460 毫秒。
- 动尾巴 `sleep-tail-flick`：1600、450、180、180、450、1140 毫秒。

打哈欠采用用户提供的 `yawn (1).gif`，原文件字节复制到 `frames/yawn.gif`，由运行时解码其透明度、处置方式与原始时长，共 12 帧、5300 毫秒，不循环。

作息使用电脑本地时钟；闲置计时使用进程内单调时钟，见项目 README 的自动作息说明。按完整团睡循环抽样 80% / 10% / 10%，避免半轮切换。原始素材校验摘要保存在项目 `qa/rest-yawn/source-hashes.json`。
