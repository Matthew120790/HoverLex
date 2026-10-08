HoverLex v0.12.1：Windows 英语阅读与输入助手。

支持 Windows 10/11 x64，需要 .NET Framework 4.8。免费模式无需 API 密钥；DeepSeek 模式使用你自己的密钥。

下载后使用：

1. 推荐下载 **HoverLex-Setup-v0.12.1.exe**，运行安装器，再从桌面的「HoverLex 鼠标取词」打开。
2. 也可下载 **HoverLex-Windows-x64-v0.12.1.zip**，完整解压后运行 `HoverLex/HoverLex.exe`，保留全部组件和 Dictionary 目录。
3. 点击「体验取词」或「翻译练习」试用。自动英文检查需要开启「输入助手」和「英文纠错」。

主要变化：英文检查状态提示、失败重试、输入读取恢复、超时与有限重试，以及重复替换和旧结果防护。

安装版使用 GitHub 的 HTTPS 签名清单检查后续更新；既有本地版本的更新设置保留。`latest.json` 是更新器使用的文件，`SHA256SUMS.txt` 提供下载文件校验值。

公开包不包含个人生词、设置、API 密钥和发布私钥。首次和重复安装、自检及本机纠错测试通过；部分跨应用键盘回归仍有失败和焦点中断，完整范围见仓库的 VALIDATION.md。

GitHub 自动提供的 **Source code** ZIP/TAR 是源码；日常使用请下载上面的 EXE 或 Windows-x64 ZIP。
