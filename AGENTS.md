# 项目协作约定

- 用中文简洁回复，面向编程新手解释。
- Windows 安装与图标更新使用 PowerShell 静默流程，不使用 computer-use 或模拟双击。
- 自制安装器支持 `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` 和 `/S`，不能按第三方打包器猜测参数。
- 安装前后校验生词与设置；桌面和开始菜单快捷方式指向固定启动器，图标指向当前版本的主程序。
- 优先运行 `scripts/install-silent.ps1`；纯检查用 `-AuditOnly`。仅图标仍旧且用户已授权时使用 `-RestartExplorer`。
- 删除文件、强制推送、覆盖未保存修改等风险操作先确认。
- 遵守当前执行环境的写入范围，不能通过子进程绕过权限限制。受限环境测试必须同时指定工作区内的安装、桌面和开始菜单目录，并使用 `-SkipIconRefresh`。
