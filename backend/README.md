# HanekawaPet 本地 AI 后端

.NET 9 / ASP.NET Core 独立项目，无第三方包。桌宠自动启动它；也可以单独运行用于开发。只监听 IPv4 回环地址，不开放跨域。

```powershell
dotnet run --project backend/HanekawaPet.Backend.csproj -- --port 17281 --data-dir E:/HanekawaPet/local-ai-data
```

生产打包由根目录 `build.ps1` 完成。独立部署需要 .NET 9 ASP.NET Core 运行时；桌宠压缩包已携带。数据文件与程序文件分开，打包不包含个人密钥和聊天记录。

## 认证与配置

除 `/health` 外，每个请求必须带 `X-Pet-Token`。通过进程环境变量 `HANEKAWA_LOCAL_TOKEN` 提供至少 32 字符的随机令牌；未提供则启动时生成到数据目录的 `local-token.txt`。只在本机开发使用，不把这个文件提交仓库。桌宠通过子进程环境传入令牌，不将它写入文件或命令行。

`HANEKAWA_AI_API_KEY` 可为 AI 密钥提供环境变量备用值。Windows 上通过配置接口保存的密钥优先，并由当前用户 DPAPI 加密；不支持在其他 Windows 账户解密。

`PUT /api/config` 请求示例：

```json
{"baseUrl":"https://your-provider.example/v1","model":"your-model","apiKey":"填自己的密钥","temperature":null,"maxTokens":null,"timeoutSeconds":120}
```

密钥省略或空白保留原值；`clearApiKey:true` 清除原值。GET 仅返回 `hasApiKey`，不返回密钥或密文。没有保存密钥时，环境变量仍可生效。不要在命令记录、源码或聊天中填真实密钥。

## HTTP 接口

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/health` | 状态和版本 |
| GET / PUT | `/api/config` | 读取非敏感配置 / 更新配置 |
| POST | `/api/config/test` | 发一次简短模型请求测试连接 |
| GET | `/api/knowledge` | 文件分类、错误、字符数和切片数量 |
| POST | `/api/knowledge/import` | JSON `{fileName,kind,content}`，kind 为 persona/documents |
| DELETE | `/api/knowledge?id=persona%2Fname.md` | 删除知识库副本 |
| GET / POST | `/api/conversations` | 最近 100 个对话 / 新建对话 |
| GET / DELETE | `/api/conversations/{id}` | 读取 / 删除对话 |
| POST | `/api/chat/stream` | JSON `{conversationId,message}`，返回 SSE |

SSE 每条事件为 `data: <JSON>\n\n`。事件类型：`sources` 带本轮片段列表、`delta` 带增量 text、`done` 表示已保存完整对话、`error` 带可显示的 message。客户端取消连接会取消上游请求，未完成回复不提交历史。不要把部分 delta 当作保存成功，必须收到 done。SSE 启动前的错误为 HTTP 400/500 和 `{error}`。

请求体最多 8 MB，知识库每文件 2 MB，角色总量 24000 字符。角色文件经用户主动配置后作为系统指令；检索资料只作事实上下文。上下文选择、提示词顺序和预算在 `AiClient.Prompt`；轻量中英文检索在 `KnowledgeStore.Search`，没有向量数据库或额外 embedding 调用。

多轮发送串行处理，成功后原子保存最近 200 条历史消息。程序重启后保留配置、知识库和聊天；随机网络端口与访问令牌不复用。文件夹中的直接编辑会在下一次读取时生效。

协议依据：[Chat Completions 官方参考](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)。为兼容第三方服务使用 Chat Completions。仅显示标准 content，不将 reasoning_content 当作角色回复。

## 验证

根目录运行 `python qa/test_backend.py`。测试临时启动模拟兼容模型与本地后端，检查三种文件、中文检索、提示词和历史、流式生成、取消、错误、密钥保护与路径限制；不需要真实密钥，不消耗真实模型额度。请先 `dotnet build backend/HanekawaPet.Backend.csproj`。


## 联网搜索

`WebSearch` 使用公开 Bing RSS 入口。显式搜索、新闻/天气/最新等关键词触发，普通闲聊或明确不要联网时跳过；无新增 AI API 字段，不依赖模型支持工具调用。只将当前问题提炼成最多 500 字符的搜索词，不把 persona/history/documents 发给搜索引擎。最多读取 512 KB，拒绝 DTD，过滤非 HTTP(S) 网址并去重，最多 5 条摘要，每条最多 1200 字符。12 秒超时，客户端取消会传播到搜索请求。

SSE 在搜索期间发出 `status`，之后 `sources` 包含 W 开头的网页来源，其 location 为实际网址。网页内容在本轮用户资料消息中标记为不可信参考；搜索失败/无结果时由后端直接回复暂时无法核实，不让模型据此断言不存在。历史仍只在完整回复后保存。

`HANEKAWA_SEARCH_ENDPOINT` 可指定返回 RSS 的搜索网关或本地测试端点，程序追加 URL 编码的 `q` 参数，默认无需配置。搜索使用独立 HttpClient，不携带 AI 密钥。

`python qa/test_backend.py` 包含搜索路由、中文 RSS、摘要注入、错误/空结果/DTD/过大响应与取消测试。真实公开搜索连通性可运行 `python qa/test_web_live.py`（AI 使用本地模拟服务，只有公开搜索请求出网）。


0.10.1 修复中文“你再去搜”等触发与检索词清洗，使用查询词的中文双字片段/英文词过滤明显无关的 RSS 结果。`WeatherLookup` 优先处理带明确城市的天气/温度请求，使用 Open-Meteo geocoding + forecast 的当前数据和三日预报，拒绝时间偏差超过 3 小时的当前数据；失败回退 RSS。天气模型值不作为气象站实测。开发环境可用 `HANEKAWA_WEATHER_DISABLED=1` 禁用此分支，或使用 `HANEKAWA_WEATHER_GEOCODING_ENDPOINT` 与 `HANEKAWA_WEATHER_FORECAST_ENDPOINT` 注入本地 JSON 测试服务。

`qa/test_real_ai.py` 仅在用户授权后运行：复制本地加密配置、角色知识与历史到项目内隔离目录，用真实搜索和用户配置模型测试两条原始问题。不回显密钥，不修改原历史，会产生正常模型用量。


0.10.2：`SearchPlanner` 在含糊的搜索追问中读取最近 10 条用户消息，使用当前模型规划短 JSON 检索词，最多 256 输出 tokens、12 秒规划时限。忽略旧助手的事实断言。规划异常回退最近明确主题，泛指词被拒绝。SSE status 增加 query 供测试追踪。含数字且包含汉字/字母的简短实体名也会触发搜索；纯数字和常见密钥前缀不按短实体处理。首次明确搜索仍直接提炼主题，无需额外规划调用。角色设定仍仅用于最终回答。

连续实测补充：将“第二季/第2部”等序数及空格归一化后匹配用户原始实体名，避免规划器无意扩大查询。空结果/搜索异常时后端直接生成“暂时无法核实，不代表不存在”的回复，不再交由模型自由发挥。

0.10.3：主 RSS 请求失败、超时或没有相关条目时，自动依次尝试 Google News RSS 和 Bing News RSS。真实测试中 Bing News 也会间歇返回 HTML，因此跨服务回退优先。各入口最多 7 秒，检索总时限 24 秒；天气分支最多 8 秒后回退。Bing 新闻跳转链接解出真实发布者网址，不访问结果页面；来源发布时间随摘要提供。自定义 `HANEKAWA_SEARCH_ENDPOINT` 默认仅请求指定网关，只有显式设置 `HANEKAWA_SEARCH_FALLBACK_ENDPOINT` 才为其添加备用入口，方便本地隔离测试。默认入口不需要这些变量。

数据根目录 `last-search.json` 仅保留最近一轮的查询词、版本、进程号、入口状态、响应字节/条目/相关结果数量和失败原因，不含 AI 密钥、提示词或响应正文。`/health` 返回实际程序集版本；桌面启动后端时写入 `runtime-info.json`，便于查明启动路径和旧版本混用。聊天窗口显示现有 SSE status；“再查呢”等简短追问也继承话题。

新增 `python qa/test_search_fallback.py` 用本地模拟验证主入口 HTTP 错误、无关网页、非 RSS 响应均可回退，密钥不发给搜索入口；`qa/test_search_window_real.py` 需用户授权，复制加密配置及历史后，通过 WPF 聊天窗口与气泡自身的 Send 方法调用实际 AI 和搜索，保存截图与结果，不修改原始历史。该项是应用内部集成验证，不等同于外部鼠标键盘操作。
