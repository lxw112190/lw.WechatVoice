# 账号自动扫描与选择

2026-11-09，preview13。

启动时不再要求先浏览目录：工具按 Moect 的思路自动定位微信 4.x 数据根目录 `xwechat_files`，把本机已登录过、含 `db_storage` 的账号列进“微信账号”下拉框，按最近使用时间排序，直接选择即可。旁边保留“选择位置”手动指定，可继续选单个账号文件夹、`db_storage` 或 `xwechat_files`。

## 扫描来源

定位数据根目录依次检查（参考 Moect `wx4/paths.py`，本工具为 C# 独立实现）：

- `%APPDATA%\Tencent\xwechat\config\*.ini` 中的保存位置（`MyDocument:` 解析为“文档”真实路径，含 OneDrive 重定向；也接受绝对路径）；
- 注册表 `HKCU\Software\Tencent\Weixin` / `WeChat` 的 `FileSavePath`；
- “文档”目录（经 `User Shell Folders\Personal` 解析真实位置）；
- 兜底浅层扫描固定/可移动磁盘下的 `Users`、`Documents`、`WeChat`、`Tencent`、`ProgramData` 一层，查找 `xwechat_files`。

账号目录名去掉末尾 4 位十六进制后缀显示（`wxid_xxx_1a2b` → `wxid_xxx`），跳过 `all_users`，按 `db_storage` 内文件最新修改时间排序（扫描有 20000 个文件上限，只做目录名和时间戳枚举，不打开数据库）。

## 行为

- 启动自动扫描一次；“扫描账号”按钮可重新扫描，尽量保留当前选择的账号，扫描不到时才选最近使用的账号。
- 上次连接成功的账号位置仍写入 `workspace/account-path.txt`，扫描命中即恢复；手动位置不在扫描范围内时仍补进列表并选中。
- 扫描只读取文件夹名和修改时间，不读密钥、数据库或聊天；扫描结果只进屏幕日志，不进诊断日志。
- 连接、HMAC 验证、密钥提取等原有流程不变。

## 验证

- 合成 `xwechat_files` 根：只列出含 `db_storage` 的账号、跳过 `all_users`、按最近使用排序、wxid 后缀裁剪/保留边界、`FromPath` 恢复账号根或 db_storage 路径；
- DesignSurface、真实消息循环合成导出流程（含新按钮裁切检查）与既有核心测试全部通过；
- 本机实际扫描命中 `D:\WeChatData\xwechat_files`（微信自定义数据位置）并列出 2 个账号，最近使用排序正确。未读取真实账号数据。
