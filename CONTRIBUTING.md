# 参与开发

先阅读 [构建说明](docs/build.md) 和 [架构](docs/architecture.md)。VS2022、C++ x64 工具链、.NET Framework 4.8 开发包、Python 3.12 x64 是开发依赖；最终程序不依赖 Python。

1. `python tools/restore-dependencies.py` 恢复并校验依赖。
2. `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -SkipWorker` 编译并运行合成回归。
3. 修改打包、依赖或 CI 后运行 `python tools/check-repository.py`，再走完整发布构建。

PR 描述说明问题、最终行为和验证结果。格式变化必须增加合成用例，不能跳过页认证、WAL 校验或 SQLite 完整性检查来换取“连接成功”。不将合成测试或上游测试写成本工具真实版本兼容结论。

测试只能使用合成数据、公开样本或用户明确授权的账号。不要提交密钥、内存转储、数据库、语音、识别文字、账号路径或真实诊断包。报告连接问题时使用程序的“导出诊断包”，仍请自己检查内容后再公开。

自有代码采用 MIT；第三方代码、二进制和模型保留各自许可证与出处。不要把研究参考的许可自动替换为本项目许可。
