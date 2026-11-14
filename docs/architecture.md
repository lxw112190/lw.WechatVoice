# 架构

```text
WinForms 主程序（会话、分页、日期、试听、导出与诊断）
  -> stdin/stdout NDJSON 协议
     VoiceEngine 独立进程
       -> 只读来源 / 每库冻结主库与 WAL
       -> HMAC / AES / WAL 校验、有效页重建、quick_check
       -> Windows winsqlite3 只读查询、索引、转写缓存
       -> SILK 独立解码进程 + silk.dll -> WAV / CSV / NDJSON
       -> AsrWorker 独立进程 -> sherpa-onnx -> SenseVoice int8
```

界面不直接加载大模型。识别工作进程有取消和超时控制，同一批次复用模型。标准控件及 Designer / resx 保留，构造阶段不查询微信或加载原生依赖。

`worker/` 是 Python 参考实现和回退构建材料。两种引擎共用合成数据，核心密码学与快照逐字节对照；参考实现不是现行部署依赖。

密钥只通过内存和标准输入传递，缓存使用当前用户 DPAPI，不写命令行或日志。快照、会话索引与文字缓存均属私密运行数据，不进入 Git 或发布包。导出时冻结会话、日期与 ID，不受异步排序或翻页影响。

来源只读不等于零资源影响。冻结副本会读取源文件，所有重建和写入发生在工具工作目录。每个数据库独立快照，不承诺跨库事务一致性。当前只支持 Windows x64，不把源码公开当作 Linux/macOS 兼容声明。
