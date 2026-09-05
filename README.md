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
