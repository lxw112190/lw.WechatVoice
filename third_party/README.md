# 固定第三方资源

- `silk-sdk/`：固定 SILK SDK 源码、逐文件 provenance 和项目导出 shim。保留原始字节，避免 Git 换行转换破坏 SHA256。
- `asr/`：从既有小芽 r5 发布材料复用的固定 sherpa-onnx 2.0.1 / ONNX Runtime / DirectML / VC Runtime，以及 SenseVoice 字表与说明。每个文件由根目录 `asr-resources.json` 校验；原始许可保留在 `licenses/`。
- 239 MB SenseVoice int8 模型不进 Git。恢复脚本从模型托管方下载并校验固定大小与 SHA256；完整部署包和离线源码包仍携带该模型及 DirectML。

这些 DLL 是本项目的可再分发运行依赖，不是本机微信组件。各文件小于 GitHub 单文件限制；本项目 MIT 不替代第三方许可。不接受随意替换同名 DLL，修改依赖后应更新来源、哈希和验证记录。
