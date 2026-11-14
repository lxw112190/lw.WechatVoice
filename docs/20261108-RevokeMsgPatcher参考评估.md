# RevokeMsgPatcher 对语音导出工坊的参考价值

评估日期：2026-11-08。仅源码和规则评估，不运行补丁，不修改微信文件，不访问真实账号。

固定参考：[huiyadanli/RevokeMsgPatcher](https://github.com/huiyadanli/RevokeMsgPatcher)，master提交 `8360fe2af70ef908dfe514eb78ca75129eacc5cc`。查看README、WeixinModifier、WechatModifier、AppModifier、ModifyFinder、FuzzyMatcher、VersionUtil和内置2.1补丁规则。

## 结论

对版本识别、适配规则组织和错误分类有间接帮助。已检查的微信实现用于修改客户端模块以提供防撤回/多开功能；未发现所需的数据库密钥提取、SQLCipher页认证或语音导出流程。不能将补丁特征匹配成功等同于数据库解密成功。

## 可借鉴

1. **核心模块版本。** [WeixinModifier](https://github.com/huiyadanli/RevokeMsgPatcher/blob/8360fe2af70ef908dfe514eb78ca75129eacc5cc/RevokeMsgPatcher/Modifier/WeixinModifier.cs)以Weixin.dll版本作为应用版本。我们现在主要记录选定进程EXE版本；后续可增加已加载核心模块的版本/架构/文件指纹，以区分同名EXE、不同安装构建或模块不一致情况。文件名和指纹不代替密钥认证，不把本机磁盘上另一个安装目录当成当前进程模块。
2. **按版本范围组织规则。** [AppModifier](https://github.com/huiyadanli/RevokeMsgPatcher/blob/8360fe2af70ef908dfe514eb78ca75129eacc5cc/RevokeMsgPatcher/Modifier/AppModifier.cs)结合版本、原/修改后文件SHA1、特征匹配结果区分已支持、已修改、无法匹配。我们可独立设计只读适配描述，记录配置对象布局、候选来源、适用模块范围和实际测试状态。版本号仅是选择规则的线索，最终仍以所选数据库页认证为准；本项目指纹优先使用SHA256。
3. **匹配数量和阶段反馈。** [ModifyFinder](https://github.com/huiyadanli/RevokeMsgPatcher/blob/8360fe2af70ef908dfe514eb78ca75129eacc5cc/RevokeMsgPatcher/Matcher/ModifyFinder.cs)明确区分特征未找到、已替换、部分替换与正常匹配。我们的扫描也应区分未找到配置名称、布局解析失败、候选与目标盐不对应、认证失败、派生额度用尽，避免只有KEY_NOT_FOUND。
4. **通配符搜索思路。** FuzzyMatcher通过固定前缀定位后核对通配部分，可为已知结构的只读定位提供思路。但它匹配的是补丁代码特征，不能直接用作WCDB配置对象或密钥的定位规则；源码也未提供这些结构对应的特征。

## 对本次4.2.0.13故障

诊断已表明内存读取成功、配置对象和候选存在，故安装目录发现或单纯加快查找不太可能直接解决本次认证失败。优先级更高的工作仍是：

- 核对所选目录与当前登录账号的对应关系，区分旧目录/副本与当前数据。
- 在同一台电脑、同一账号、同一目录下用Moect进行对照，不把另一台电脑的成功套用到此环境。
- 检查候选去重、目标盐提示及派生顺序。当前64次派生额度已经用尽；需要区分总扫描完成与候选派生尚未覆盖完整，不能把limit_reached=false解释为所有候选均已充分验证。

[内置规则](https://github.com/huiyadanli/RevokeMsgPatcher/blob/8360fe2af70ef908dfe514eb78ca75129eacc5cc/RevokeMsgPatcher.Assistant/Data/2.1/patch.json)对Weixin.dll配置了多个版本区间，最新区间从4.1.12.0之后且没有上界，功能为防撤回与多开。这只是其补丁规则的选择范围，不是4.2.0.13数据库解密的实测证据。

项目为GPL-3.0。当前仅记录设计参考，未复制其源码或补丁字节到本项目。若后续直接复用源码，应先确认分发许可要求。本次不新增运行依赖、不生成软件新版。
