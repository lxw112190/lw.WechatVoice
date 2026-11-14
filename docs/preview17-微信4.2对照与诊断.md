# 微信 4.2 连接失败：Moect 对照与诊断改进

2026-11-10，preview17。此版保留 preview16 的副本加速，修复错误提示及底层诊断丢失；**不宣称已修复真实微信 4.2 账号兼容性**。

## 本次诊断包确认的事实

用户提供的诊断来自 preview15。三次请求中，第一次扫描 120 秒后触发时限、未匹配密钥；最近两次分别在约 31 秒和 21 秒完成，均匹配 **3/3 个加密数据库密钥**。

这两次后续流程相同：联系人库副本成功；media 语音库和 message 消息库三次尝试后失败；最终仅有 1/3 个可用副本。界面随后显示未找到 VoiceInfo。

因此本例不是“所有密钥都没获取到”，也没有证据证明微信改了 VoiceInfo 表名。VoiceInfo 报错是语音库副本缺失后的后续提示。旧日志只留下外层 InvalidDataException 与通用 HResult 5377，无法区分源文件持续变化、页认证失败、WAL 问题和 SQLite 检查失败，不能从这份包中继续推断唯一根因。

## 上游实际变化

固定对照提交：

- 早期参考：`3d08feceae6492fe2f4f9f8e03b02ec411970c90`
- 本次 main：[`88dffc7d0d06632f8061b0a19aa82b38337d7ee5`](https://github.com/lxw112190/Moect/commit/88dffc7d0d06632f8061b0a19aa82b38337d7ee5)

两个提交之间共 13 个提交，但以下四个核心文件逐文件内容相同：

| 文件 | 比较结果 |
| --- | --- |
| [wx4/key.py](https://github.com/lxw112190/Moect/blob/88dffc7d0d06632f8061b0a19aa82b38337d7ee5/wx4/key.py) | 无变化；Config.Cipher 名称、XOR 掩码、对象偏移和口令派生路径不变 |
| [wx4/decrypt.py](https://github.com/lxw112190/Moect/blob/88dffc7d0d06632f8061b0a19aa82b38337d7ee5/wx4/decrypt.py) | 无变化 |
| [wx4/reader.py](https://github.com/lxw112190/Moect/blob/88dffc7d0d06632f8061b0a19aa82b38337d7ee5/wx4/reader.py) | 无变化；语音仍读取 VoiceInfo |
| [core/service.py](https://github.com/lxw112190/Moect/blob/88dffc7d0d06632f8061b0a19aa82b38337d7ee5/core/service.py) | 无变化；连接仍调用 KeyExtractor 和 decrypt_all |

不能把“更新后 Moect 成功”解释为上游增加了新的 4.2 密钥算法；当前代码对比不支持这个结论。

## 两边现有的行为差异

| 环节 | Moect | 本工具 |
| --- | --- | --- |
| 进程 | 按内存排序尝试多个 Weixin PID | 仅用户选定 PID；不扩大读取范围 |
| 扫描顺序/边界 | Config 路径优先，旧 raw-key 回退，另有口令路径 | 合并首轮 raw-key/配置名扫描、再查对象；有 120 秒和 2 GiB 总读取限制 |
| 主库页 | 校验第一页密钥，然后解密其他页 | 校验每页 HMAC 后解密 |
| WAL | 按 salt、提交边界重放 | 额外检查头/帧 checksum、页认证及当前代际截断 |
| 在线副本 | 复制并检查输出可读性 | 主库/WAL 对复制前后确认源签名稳定，最多三次重试 |
| 连接成功 | 必须有会话库和消息库；不要求语音库存在 | 语音工坊需要可读的 media/VoiceInfo 库 |

这些差异可解释“上游成功而本工具失败”的可能路径，**尚不能确定本次具体触发了哪一项**。不会为了看起来连接成功而删除认证、忽略 WAL 或仅验证联系人库。

## preview17 已修改

- 语音库副本全部失败时直接提示“语音库副本创建失败，尚未读取 VoiceInfo”，保留底层异常；只有副本已经成功、字段不符才报告表结构不支持。
- 给页认证、源变化、WAL、SQLite 打开/quick_check 分配固定错误代码；页认证记录数值页号，SQLite 记录返回码和系统库版本。
- 每次快照尝试记录阶段及尝试次数，递归保留受限的内层异常类型/代码/调用位置。不会序列化异常消息或整个 Exception.Data。
- 保存微信完整文件版本和扫描的最终读失败统计，避免失败后丢失版本依据。
- 新字段进入诊断 ZIP 白名单；仍不记录账号路径、密钥、正文、语音或识别结果。
- 合成回归验证嵌套页错误能穿透快照和目录包装，日志不泄露注入的私密字段，语音库失败不再误报成表结构问题。

## 下一次复测如何定位

用 preview17 重新连接一次并导出诊断包：`SOURCE_CHANGED` 表示复制时仍在变化；`PAGE_AUTH_FAILED` 带失败页号；`WAL_*` 指明 WAL 检查；`SQLITE_QUICK_CHECK_*` 带返回码；`VOICE_SCHEMA_UNSUPPORTED` 才进入表结构适配。由实际代码决定下一步，不以版本号猜测。

本轮只分析用户提供的诊断 ZIP 和公开代码，测试使用合成数据库/进程，没有读取真实聊天或真实微信进程内存。上游源码仅放开发参考目录，未加入部署或源码包。
