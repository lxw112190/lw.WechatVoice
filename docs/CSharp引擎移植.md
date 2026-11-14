# C# 引擎移植（去掉 Python 运行时）

2026-11-09 启动。目标：把 PyInstaller 打包的 Python worker（26M CPython 运行时）替换为 C# 控制台引擎，保留现有 NDJSON 协议、双进程隔离和原生识别组件。每个阶段先与 Python 参考实现双轨对照、逐字节验证，最后才在授权真实账号上复测并移除 Python。

## 阶段

1. **cryptodb 移植与对照（已完成）**：`src/VoiceEngine/CryptoDb.cs` 按 worker/cryptodb.py 逐行移植（PBKDF2-HMAC-SHA512 256000 轮、页 HMAC-SHA512、AES-256-CBC 无填充、WAL 帧校验与重放、WCDB 旧代际逻辑结尾、冻结副本与三次重试）。`tests/crypto_fixtures.py` 生成 13 个合成用例（明文/加密 × 无WAL/WAL × 未提交尾/旧代际/截断/篡改/坏头/错密钥/真实 reserve-80 页）及密码学向量，Python 实现先跑一遍作为参考输出；`tests/CryptoDbCheck.cs` 要求 C# 移植快照**逐字节一致**、拒绝行为一致。已接入 build.ps1。
   - 移植中确认的坑：.NET Framework 的 `HMACSHA512.TransformFinalBlock` 返回输入块而非哈希（要用 `Hash` 属性）；winsqlite3 文件名必须传 UTF-8 字节（中文路径不能走 ANSI 封送）；加密页布局要求原库为 reserve-80 页（WCDB 真实格式），普通无 reserve 库不在支持范围。
2. **capture/key_reader 内存扫描与密钥提取（已完成）**：`src/VoiceEngine/Capture.cs`（进程身份/版本验证、db_storage 枚举与限制、区域枚举/限时限量读取、raw/Config.Cipher 候选、DPAPI 密钥库）与 `KeyReader.cs`（Matcher 去重与页认证、1MiB 块扫描、CONFIG_NAME 符号与节点/对象/掩码解析、受限主密钥派生）。`tests/capture_fixtures.py` 构造合成内存镜像（ASCII/宽字符字面量、Config.Cipher 节点结构、掩码 blob）并先跑 Python `extract` 作为基准；`tests/CaptureCheck.cs` 对照 C# 移植的 found/统计逐项一致，另含 candidates 边界向量、databases 限制/拒绝、DPAPI 密钥库绑定/超限/坏格式，以及**对合成 4.0.5.16 进程的完整 Win32 扫描**（同一镜像 pin 入进程内存、节点指针改写为真实地址后扫出两条密钥：raw 字面量路径 + Config 主密钥派生路径）。已接入 build.ps1。
   - 移植中确认的坑：`.NET` 正则无法直接匹配字节串，块需按 Latin-1 解码后再跑等价正则；合成进程里 Config 节点内嵌的符号/对象/数据指针必须改写成真实地址，偏移值只在 base=0 的参考镜像里成立。
3. **library/speech_cache 索引、查询与识别缓存（已完成，导出清单待阶段4后接上）**：`src/VoiceEngine/NativeSqlite.cs`（winsqlite3 薄包装：只读 URI、exec、预编译语句、UTF-8 绑定、列类型感知——C 库按既定方针直接使用）；`Library.cs`（索引建库、联系人/消息/语音目录、发送者解析全路径、分页/筛选/白名单排序含识别缓存联接、payload、文件名清洗）；`SpeechCache.cs`（transcripts 缓存 READY/EMPTY/ERROR 语义）。`tests/library_fixtures.py` 构造覆盖发送者解析矩阵（server 命中/本地回退/server 不匹配不回退/歧义未知/未知会话/坏结构 issue）的合成 db_storage 并先跑 Python 参考；`tests/LibraryCheck.cs` 对照 build 报告、voices 全字段、全部排序方向、日期/ids/空ids/非法请求拒绝、缓存联接排序、缓存命中/摘要失配/ERROR 不满足查找/超页拒绝、损坏缓存告警与降级、payload 字节与文件名清洗。已接入 build.ps1。
   - 移植中确认的坑：`str(svr or '')` 语义要求按列类型区分（NULL 和整数 0 → 空串，文本 '0' 保留），winsqlite3 包装必须暴露 column_type；`.NET` 没有 `Marshal.PtrToStringUTF8`，UTF-8 指针需手工解码；JSON 数组反序列化在 net48 得到 ArrayList。
4. **SILK 解码与导出/清单接线（已完成）**：SILK SDK C 源码按 pysilk 0.2.8 的 submodule 提交 `507be6bc`（kn007/silk-v3-decoder）固化到 `.deps/silk-sdk/`（138 个文件逐一 SHA256 + 来源清单），MSVC 编译为 silk.dll（固定哈希，`build/silk-build.bat` 用相对路径与 vcvars64，无中文路径依赖）。`VoiceCodec.cs` 逐包语义复刻 pinned 0.2.8 Cython 包装（读至 EOF、仅剥离已验证的 -1 结束符、1920 字节帧缓冲、90 秒上限、WAV 回读验证、隔离子进程 8 秒超时）；`Transcriber.cs` 按原有 AsrWorker 行协议驱动本地识别；`Export.cs` 移植导出管线（CSV/NDJSON/清单/partial、公式前缀防护、缓存复用、取消语义）；`Program.cs` 支持 `--decode` 子进程与 UTF-8 stdin/stdout。`tests/codec_fixtures.py`+`CodecCheck.cs`：5 种帧变体（多包/无前缀/-1 终止/单包/CRLF）WAV **逐字节一致** + 4 类拒绝 + 隔离子进程与取消清理；`tests/export_fixtures.py`+`ExportCheck.cs`：清单字段、CSV 字节、WAV/SILK 哈希、journal 行数、文件名集合、取消 partial 路径全部一致，**真实 SenseVoice 模型**识别导出（文字与命名）与 Python 参考一致（asr 资产暂存后运行，用后移除）。已接入 build.ps1。
   - 移植中确认的坑：WAV 回读验证要把 data 块字节数与 PCM 长度直比（Python 的 nframes 是帧数）；重定向 stdin 默认 OEM 码页，协议须显式 UTF-8；JavaScriptSerializer 反序列化会把 `\r\n` 归一为 `\n`（字节级内容对比要直接读文件）；silk.dll 编译须在纯 ASCII 工作目录。
5. **打包替换（已完成）与授权真实账号复测（待办）**：`Program.cs` 移植 cli.py 的完整 stdin/NDJSON 协议（connect/keys/catalog/list/count/export/recognize_page/preview/self-test、取消标记、退出码 0/1/2）与 worker 侧诊断日志（`WorkerDiagnostics.cs`，白名单字段/轮转/仅函数与行号）。打包产物改用 C# 引擎（engine/VoiceWorker.exe + silk.dll），IpcCheck 与真实消息循环合成 GUI 流程、ZIP 验收全部通过（preview14）。**授权真实账号复测仍未做**：此前 Python 引擎包（preview13 及更早）可作回退，Python worker 保留在构建系统中；合成对照通过不宣称真实账号兼容。

## 约束

- 对照基准固定为当前仓库的 Python worker（与 Moect 固定提交 3d08fec 的格式参考不冲突）；移植期间双实现并存，任何一方改 WAL/加密语义都要重跑对照。
- 快照/解密测试只用合成数据，不读取真实账号、密钥或进程内存。
- 最终替换必须通过授权账号真实复测，不因合成对照通过就宣称兼容。
