# 小白服务器管理器

基于 .NET 8 WinForms 的 Windows 服务器远程管理工具，面向多台服务器的日常运维场景。

## 当前能力

- SSH / RDP 连接管理与实时延迟检测
- Linux SSH 登录、系统信息、远程重启和 SSH 端口安全迁移
- 远程重启及重启状态验证
- SSH、RDP、HTTP/HTTPS 等端口检测与修改
- MySQL、MariaDB、MongoDB、Redis 的数据库连接、用户权限、备份恢复和迁移
- Linux 目标机通过 apt/dnf 一键部署 MySQL、MariaDB、MongoDB、Redis（需通过实机验收）
- 通过 SSH 直接部署常用数据库版本
- 启动锁与 AES-256-GCM 加密保险库

## 构建

环境要求：Windows、.NET 8 SDK。

```powershell
dotnet restore RDPManager.sln
dotnet build RDPManager.sln -c Release
```

程序运行时产生的 `servers.xml`、`servers.vault` 和其他凭据文件属于本机数据，不应提交到版本库。

## 安全边界

- 项目不部署 Agent，不依赖中转服务器。
- 数据库管理通过 SSH 隧道完成。
- 当前不提供一键卸载数据库、删除数据库目录或删除数据库数据的功能。
- Oracle 相关入口暂保留，数据库管理和一键部署尚未实现。

## Linux SSH 终端

Linux 服务器的“连接”功能使用程序内置的 SSH.NET 交互会话和 xterm.js 终端，不调用 CMD、Windows `ssh.exe` 或 PuTTY。终端窗口标题只显示管理器中保存的服务器名称和连接状态。

终端界面会隐藏目标服务器 IP、SSH 端口以及连接字符串；远程输出进入终端前也会过滤当前目标 IP 和 SSH 端口。终端内容只保留在当前会话中，不写入管理器日志，也不通过剪贴板传递服务器密码。服务器本身的 SSH 审计日志仍由 Linux 系统按其安全策略记录。

内嵌终端使用 WebView2 承载 xterm.js。Windows 10/11 通常已经安装 WebView2 Runtime；若目标电脑没有，需要先安装对应运行时。
