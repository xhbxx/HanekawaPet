# HanekawaPet · AIGC 交互式桌宠

Windows 桌面陪伴项目：角色动画、触摸互动、AI 对话、联网查询与每日扭蛋收集。C# / WPF / ASP.NET Core / .NET 9，无第三方 NuGet 包。

!

## 下载与启动

前往本仓库 **Releases**，下载 `HanekawaPet-v版本号-windows-x64.zip`，完整解压后双击 `HanekawaPet.exe`。发行包自带运行时，不需要安装 .NET。请保留 exe 旁边所有文件夹；GitHub 自动生成的 Source code ZIP 为源码。

首次启动可直接体验动画和扭蛋；AI 对话需在右键菜单的“设置 → AI 接口与知识库”中填写自己的兼容接口地址、模型和密钥。

## 互动与动画

- 点击头顶摸头，点击脸部戳脸，点击身体弹出输入框；按住拖动播放对应拖拽动作。
- Enter 发送，Shift+Enter 换行。等待回复时播放思考动画，回答以头顶文字显示；也可打开独立聊天窗口。
- 待机、慢走、自由散步、敲屏和晃脚；日常动作随机调度，用户互动可打断。
- 本地时间 00:00–06:00、13:00–14:00 自动休息；15–25 分钟未互动打哈欠，超过 3 小时未互动休息。
- 靠近屏幕左右边缘时有 50% 概率向上爬行，随机爬一段后停止。
- 右键只显示“设置”和“扭蛋”；设置中提供大小、速度、置顶、鼠标穿透、动画导入和退出。
- 部分生成动画有点抽象

## 扭蛋与展示柜

右键 → 扭蛋：四款动画等概率随机播放，每款 25%，完整约 8 秒后揭晓结果。奖池包含 **10 张动漫／游戏印象图标（后续大概率会优化），每张 10%**。每天限抽一次，北京时间 00:00 刷新；重复藏品累计数量，展示柜分层陈列，点击可查看大图。预览动画不扣次数。

抽取结果在播放前保存，中途关闭不会丢失，重新打开可查看上次结果。修改 `gacha/catalog.json` 可增补奖品及权重，编号应保持稳定；格式见 [扭蛋素材说明](gacha/README.md)。

## AI 与本地数据

兼容 OpenAI 风格的 Chat Completions 接口，支持 SSE 流式回复、多轮历史以及 MD / JSON / JSONL 角色与资料文件。角色设定每轮生效，资料通过切片及关键词加权检索提供上下文，无需向量数据库。

联网查询使用公开 RSS 搜索与多个入口回退，天气优先通过 Open-Meteo 获取。只读取标题、网址与摘要，不读取网页全文。服务可用性受本地网络影响，查询失败时提示无法核实。

设置、收藏、知识库及聊天保存在 `%APPDATA%\HanekawaPet`，与程序文件分开。AI 密钥按当前 Windows 账户 DPAPI 加密；本地后端仅监听回环地址，并使用随机访问令牌。上传源码或发布包不包含个人数据。

- [AI 接口与知识库指南](AI-GUIDE.md)
- [动画包格式](ANIMATION-PACK.md)
- [后端开发说明](backend/README.md)

## 开发与发布

开发环境：Windows x64、PowerShell 7、.NET 9 SDK（包括 Windows Desktop 与 ASP.NET Core 运行时）。

```powershell
dotnet run --project HanekawaPet.csproj
./build.ps1 -SkipArchive
./scripts/Test-Desktop.ps1
./scripts/Package-Release.ps1
```

源码结构：`Core/` 负责资源、行为、配置和收藏，`Platform/` 处理桌面能力，`backend/` 提供模型与检索接口，`pets/` 与 `gacha/` 存放素材，`qa/` 包含隔离测试及 GIF 样例。

GitHub Actions 在主分支与 PR 上构建验证；推送匹配项目版本的 `v*` 标签后，自动生成 Windows ZIP、SHA256 校验文件和 Release 草稿。操作见 [GitHub 上传与 Release 方案](docs/RELEASING.md)。

项目采用 AI 辅助开发与 AIGC 素材迭代，素材说明及源码许可状态见 [ASSETS.md](ASSETS.md)，版本记录见 [CHANGELOG.md](CHANGELOG.md)。
