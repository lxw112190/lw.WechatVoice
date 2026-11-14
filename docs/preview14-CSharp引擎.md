# C# 引擎替换 Python 运行时

2026-11-09，preview14。

部署包的后台引擎从 PyInstaller 打包的 Python worker 换成本项目自研的 C# 引擎（VoiceEngine），**去除 CPython 运行时**；引擎协议、双进程隔离、隔离子进程解码和本地识别组件保持不变，界面代码无需改动。C/C++ 库按需保留：Windows 系统自带 SQLite（winsqlite3）和自编译的 SILK SDK（silk.dll，164KB）。

## 引擎构成

- 加密/快照（SQLCipher4 页格式手写实现）、WAL 冻结与代际结尾：CryptoDb.cs
- 进程内存扫描与密钥提取、DPAPI 密钥库：Capture.cs / KeyReader.cs
- 索引、分页、筛选、排序与识别缓存：Library.cs / SpeechCache.cs / NativeSqlite.cs
- SILK 解码（pysilk 0.2.8 同源 SDK C 核心编译为 silk.dll，逐包语义复刻其 Cython 包装）：VoiceCodec.cs
- 本地识别子进程驱动（AsrWorker 不变）：Transcriber.cs
- 导出/清单/取消：Export.cs
- 诊断日志（白名单字段、轮转、无正文无路径）：WorkerDiagnostics.cs

## 对照与验收

移植按阶段与 Python 参考实现双轨对照，全部通过后才打包（详见《CSharp引擎移植.md》）：

- 13 个加密/WAL 合成用例快照**逐字节一致**；
- 合成内存镜像 extract 结果与统计一致；合成 4.0.5.16 进程完整 Win32 扫描扫出两条密钥（raw + Config 主密钥派生路径）；
- 索引/查询/全排序/缓存语义一致；
- 5 种 SILK 帧变体 WAV 与 pysilk 0.2.8 **逐字节一致**；
- 导出清单字段、CSV 字节、WAV/SILK 哈希、取消 partial 路径一致；
- **真实 SenseVoice 模型**识别导出文字与命名一致；
- 打包产物：IpcCheck（UTF-8 协议、数量、取消映射、诊断 ZIP）与真实消息循环合成导出流程（FormsCheck）通过；ZIP CRC/哈希/隐私排除/9 条音频变体导出通过。

## 范围与限制

- **尚未在授权真实账号上复测**：本轮全部为合成数据对照，不宣称真实账号兼容；preview13 及更早的 Python 引擎包可作为回退，Python worker 仍保留在源码构建系统中（build.ps1 默认仍产出）。
- 合成对照通过不等于微信新版本兼容；真实账号适配仍须实际验证。
