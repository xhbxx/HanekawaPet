# 扭蛋素材

四款扭蛋动画已接入，每次从珊瑚红、青色、紫色、金色中等概率随机播放一款，完整一轮约 8 秒。预览不扣次数。奖池包含 10 张动漫／游戏主题印象插图，默认等概率，每张 10%，可正式抽取。

`catalog.json` 示例（文件需要真实存在；编号是收藏的稳定标识）：

```json
{
  "animations": ["animations/gacha-coral.gif", "animations/gacha-cyan.gif", "animations/gacha-violet.gif", "animations/gacha-gold.gif"],
  "prizes": [
    { "id": "item-01", "name": "物品名称", "image": "images/item-01.png", "description": "物品介绍", "weight": 1 }
  ]
}
```

动画使用 GIF 自带时长，完整播放一轮后展示结果；也兼容旧的单个 `animation` 字段。原始 GIF 保留，运行时按显示区域缩放解码画面以减少内存。没有动画时保留空白区域，等待约 1.2 秒后展示结果。没有奖品或插图缺失时不能抽取，也不会扣次数。图片支持 PNG/JPG/GIF/BMP，最大 8 MB、4096 像素，所有路径必须相对本文件夹。

所有奖品默认等概率，允许重复，重复奖品在柜中累计数量。北京时间每天 00:00 刷新一次；记录与获奖插图副本保存在 `%APPDATA%\HanekawaPet\gacha`，不随发行包替换。抽取先保存结果和次数，再播放动画；关闭或退出中断时，下次打开可查看已保存的结果。
