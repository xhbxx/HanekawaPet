# GitHub 与 Release 发布

## 首次上传

使用整理好的源码目录创建空 GitHub 仓库，仓库名可用 `HanekawaPet`。在源码目录运行（替换下方地址）：

```powershell
git init -b main
git add .
git status --short
git commit -m "Initial desktop pet release"
git remote add origin https://github.com/YOUR_NAME/HanekawaPet.git
git push -u origin main
```

源码仓库只放程序、素材、文档和测试。`dist*`、`artifacts`、运行时、配置密钥、对话及构建缓存由 `.gitignore` 排除。不要直接上传旧开发目录或其历史 ZIP。

## 自动发布

1. 修改 `HanekawaPet.csproj` 的版本、`docs/RELEASE-NOTES.md`，提交并推送。
2. 标签必须与项目版本一致；首版运行 `git tag -a v0.12.0 -m "v0.12.0"`，再运行 `git push origin v0.12.0`。
3. Actions 在 Windows 上编译，执行桌面检查及本地模拟后端检查，通过后生成便携 ZIP 和 `SHA256SUMS.txt`，创建 **Draft Release**。
4. 在 GitHub Releases 查看草稿，下载并启动 ZIP，核对说明后点击 Publish release。

普通主分支提交和 PR 只构建验证并提供 Actions artifacts，不创建 Release。公开版本不覆盖，修复应使用新版本标签；同一个草稿可重跑上传。工作流只使用仓库自动提供的 `GITHUB_TOKEN`，无需放入个人令牌或 AI 密钥。

## 本地发布

需要 Windows x64、PowerShell 7、.NET 9 SDK（包括 Desktop/ASP.NET Core 运行时）。

```powershell
./build.ps1 -SkipArchive
./scripts/Test-Desktop.ps1
./scripts/Package-Release.ps1
```

发布文件在 `artifacts/`。完整目录必须包含 exe、dll、pets、gacha、backend、assets 和 runtime；用户只需要解压并运行 exe。`Get-FileHash <zip> -Algorithm SHA256` 可对照校验文件。检查数据使用项目内隔离目录，不消耗个人扭蛋次数。

工作流已在本地使用同样的构建与桌面检查脚本验证；GitHub 云端首次执行结果应以 Actions 页面为准。

参考：[setup-dotnet](https://github.com/actions/setup-dotnet)、[GitHub CLI 创建 Release](https://cli.github.com/manual/gh_release_create)。
