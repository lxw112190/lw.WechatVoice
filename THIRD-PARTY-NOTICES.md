# 第三方参考和运行依赖

本工具当前实现为独立 WinForms + C# 工作进程和 C/C++ 原生库；Python 保留作构建期参考和回退。参考的第三方项目没有被作为整套应用执行。

2026-11-10 对照 Moect 固定提交 `88dffc7d0d06632f8061b0a19aa82b38337d7ee5`，与早期参考版本逐文件比较：wx4/key.py、wx4/decrypt.py、wx4/reader.py、core/service.py 内容相同。本次记录连接成功判定与校验策略差异，改进本工具的结构化错误诊断，没有复制新的上游实现或降低原有认证校验；不能据此声称本工具微信 4.2.x 实测兼容。

2026-11-08 preview8重新在线核对Moect的固定提交与wx4/key.py，确认已有raw-key/Config.Cipher/认证路径，解除本项目4.0.5.16单版本门禁，允许4.x按实际数据认证尝试；没有引入Moect源码或新增依赖。Moect自身也不保证全部4.x版本。既有本工具真实账号实测仅4.0.5.16，其他版本不可写成已实测通过。

preview4实际复用用户提供的“小芽·儿童语音伙伴 r5”中的UTF-8 sherpa配置封装和SenseVoice CPU识别路径，并打包sherpa-onnx 2.0.1、SenseVoiceSmall int8模型及其字表。模型SHA256为`c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51`，约228MiB。`asr-resources.json`记录每个实际运行文件的原始大小/哈希和小芽来源，完整许可在licenses：sherpa-onnx为Apache-2.0、SenseVoice为MIT；保留ONNX Runtime、DirectML、原生第三方和微软运行库相应许可。CPU识别不代表DirectML/GPU识别；复用的运行库构建依赖DirectML，所以保留对应DLL和许可，但不启用其GPU Provider。不包含小芽个人设置、API密钥、录音、聊天、TTS及唤醒模型。

2026-11-07 对照 [lxw112190/Moect](https://github.com/lxw112190/Moect/tree/3d08feceae6492fe2f4f9f8e03b02ec411970c90)，固定提交 `3d08feceae6492fe2f4f9f8e03b02ec411970c90`（GPL-3.0）。查看 `wx4/reader.py`、`wx4/key.py`、`core/media_codec.py`，核对数据库字段、密钥字面量和 SILK 帧行为；未复制或打包其源码、前端、缓存或可执行程序。此处修正为本项目独立实现；Moect 使用的解码包装与本项目 pysilk 0.2.8 Cython 包装不同，不能照搬其单包复制/裁切与结束标记策略。未引入新的运行依赖。

| 项目 | 版本 / 固定依据 | 用途与许可 |
| --- | --- | --- |
| [ChatTrace](https://github.com/qiaodogbear/ChatTrace) | `f9fcdb42e8fb99a527c0bcd7d6ff7ea4bc891563` | 参考数据库、SILK 和 SQLCipher 格式、解码候选。保留 MIT 原文。其 4.1.x 实测不能证明本工具的 4.0.5.16 已兼容。未采用 Frida、注入或图片解密。 |
| [silk-python / pysilk](https://github.com/synodriver/pysilk) | 0.2.8，cp312 win_amd64 wheel，原始 SHA256 见锁文件 | SILK 转 PCM。PyPI metadata 声明 BSD；该 wheel 未附独立 LICENSE，随包保留原始 METADATA、所链接 SILK SDK 完整许可及 decoder MIT 许可。只打包 CPython/Cython 后端。 |
| [kn007/silk-v3-decoder](https://github.com/kn007/silk-v3-decoder) | pysilk 上游子模块引用 `07bfa0f56bbfcdacd56e2e73b7bcd10a0efb7f4c` | decoder 外层 MIT；SILK SDK 为 Skype 原始 BSD 风格许可，含专利权不授予条款，见 licenses/SILK-SDK.txt。 |
| SILK SDK C 原生引擎 | 固定提交 `507be6bca8ce1fb977a061481f1d79e8c610e309`，138 个源文件逐一 SHA256，见源码 `.deps/silk-sdk/silk-sdk-provenance.json` | 部署中的 silk.dll 由这些源文件及项目自有导出 shim 编译；原始 SDK 完整许可见 licenses/SILK-SDK.txt。来源清单中的 silk_dll_sha256 是最初移植构建记录，当前发布 DLL 哈希见 dependency-provenance.json 的 native_engine.silk_runtime_sha256。 |
| [PyCryptodome](https://github.com/Legrandin/pycryptodome) | pycryptodomex 3.23.0 | AES；BSD / public domain 组合，完整 LICENSE.rst 随包。 |
| cffi / pycparser | 2.1.1 / 3.12 | silk-python 声明的构建/可选后端依赖，wheel 和完整许可保留；当前部署包排除 CFFI 后端。 |
| CPython | 3.12.7 x64 | PSF 及历史许可，随 Python 原文。标准库间接依赖 OpenSSL 3.0.15、bzip2 1.0.8、libffi 3.4.4、xz/liblzma 5.4.6、zlib 1.2.13、SQLite 3.45.3；相应原始许可随包。 |
| [PyInstaller](https://github.com/pyinstaller/pyinstaller) | 6.21.0；hooks 2026.6 | 构建工作进程；GPL + bootloader exception，完整原文随包，未修改 bootloader。 |
| Microsoft runtime | VCRUNTIME140 / UCRT / API-set DLL | Python 工作进程随带的 Microsoft runtime 文件；适用 Microsoft Visual C++ / Windows SDK 运行时条款，保留版本信息，不修改系统运行时。 |

ChatTrace 源码归档原始 SHA256：`1b0fbc42fd50d591765e14a0a4442a93dac6aa2cbf62b8ee2f116246dcef3e8b`。可参考 [SQLite WAL 格式](https://sqlite.org/walformat.html)、[SQLite 文件控制](https://sqlite.org/c3ref/c_fcntl_begin_atomic_write.html)；本工具额外验证页认证、WAL checksum、提交边界和副本完整性，不沿用只按 local_id 查语音的有歧义关联。

许可文件由 wheel、已安装开发环境和对应上游源码取得；`dependency-provenance.json` 记录运行文件原始哈希。源码和部署包均排除真实账号、密钥、内存转储、数据库、录音与缓存。
