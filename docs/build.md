# 构建、CI 与发布

## 环境

Windows 10/11 x64；VS2022 的 .NET 桌面开发、.NET Framework 4.8 开发包、C++ x64 工具链和 Windows SDK；Python 3.12 x64。构建脚本通过 vswhere 定位 VS，不依赖固定盘符。

## 干净克隆

```powershell
python tools/check-repository.py
python tools/restore-dependencies.py
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -SkipWorker
```

模型从公开 Hugging Face 仓库固定修订下载，校验大小与 SHA256，失败不会发布半文件。模型下载约 239 MB；Git 树内保留既有运行库（含 DirectML）、字表、原生 SDK 和许可。依赖缓存不可信任为验证结果，每次构建重新校验。

Python 四个测试 wheel 从 PyPI 按 requirements-lock.txt 恢复。Python 仅作格式、密码学与导出对照；部署默认是 C# 引擎，不运行 Python。旧的 Python 回退打包仍在 build.ps1，不作为开源 CI 默认产物。

没有网络时可使用完整离线源码包，内含 `.deps/wheels`、`.deps/asr-kids` 和 SILK 源码；依赖就绪后直接运行 build.ps1。`restore-dependencies.py --verify-only` 可检查已经恢复的资源。

VS 中打开原有 `WechatVoice.Workshop.sln`，选择 Release / x64；先恢复依赖以解决 sherpa-onnx 引用。仅 VS 编译不等于运行完整回归。

## 回归与打包

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-release.ps1
```

执行公开树检查、依赖恢复、编译、合成密码学/WAL/快照对照、SILK 编译与音频回环、UI/IPC/导出回归、打包、包内验证和源码 ZIP 独立编译。SQLite 测试支持标准 Python 的 DLLs/sqlite3.dll 与 Conda 的 Library/bin/sqlite3.dll；环境缺失会明确报错/跳过，不能标记密码学通过。

`deliveries/时间戳/` 中提供 Windows ZIP、完整离线源码 ZIP 和 `.sha256`；包内 `files.sha256.json` 列出文件校验值。公开源码快照可单独生成：

```powershell
python tools/export-repository.py --output deliveries/lw.WechatVoice-git-source.zip
```

它只导出固定文件和源码目录，不复制工作目录；普通 Git 源码 ZIP 不含模型和恢复缓存。完整部署包和离线源码包保留模型、DirectML 及许可。不要把整个本地开发目录拖到 GitHub 上传。

## GitHub Actions

`.github/workflows/windows.yml` 在普通推送、PR、手动触发时编译并上传 Artifact。只有 `push` 的 `v*` 标签才启动有写权限的 Release job，且标签必须等于 `v` 加 `VERSION`。包含连字符的预览版标为 prerelease。PR 不发布，不使用个人 Token；不访问真实微信进程和账号。

当前正式发布版本为 `1.0.0`。升级版本时同步 VERSION、原生 self-test 与界面诊断版本，并更新发布说明。发布级别不等于全部微信版本兼容，真实账号验证范围仍须如实记录，不能仅修改包名宣称正式兼容。

远程仓库由用户自行创建为 `lw.WechatVoice`。从已检查的公开 ZIP 解压后的 `lw.WechatVoice` 目录执行：

```powershell
git init -b main
git add .
git diff --cached --stat
git commit -m "Initial open-source release of lw.WechatVoice"
git remote add origin https://github.com/lxw112190/lw.WechatVoice.git
git push -u origin main
```

先看 Actions 是否通过，再推送匹配版本的标签：

```powershell
git tag v1.0.0
git push origin v1.0.0
```

日常变更由用户自行提交；仅在用户明确要求发布时执行版本提交、标签推送与 Release 发布。
