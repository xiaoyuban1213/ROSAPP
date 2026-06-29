# ROS-APP

ROS-APP 是一个 Windows 桌面对象存储管理工具，基于 WinUI 3 实现，面向 Rainyun ROS / S3 兼容对象存储场景。程序目前支持 S3 与 SFTP 两种协议，用同一套界面完成连接、桶列表、对象浏览、文件预览、上传、下载、删除、日志追踪和版本更新检测。

作者：Yuban-Network

## 功能特性

- S3 兼容对象存储连接，基于 AWS SDK for .NET。
- SFTP 连接，基于 SSH.NET。
- 支持 Rainyun 常用端点：
  - S3: `https://cn-nb1.rains3.com`、`https://cn-sy1.rains3.com`
  - SFTP: `sftp://cn-nb1.rains3.com:8022`、`sftp://cn-sy1.rains3.com:8022`
- 桶列表和对象浏览，支持类似资源管理器的文件夹/文件视图。
- 对象预览，小文件可直接在右侧预览文本内容。
- 上传和下载，带传输进度、百分比和状态提示。
- 文件右键操作：下载、删除、复制对象 Key、复制完整路径。
- 日志视图，记录连接、刷新、传输、删除、更新检测等关键操作。
- 深色/浅色模式切换。
- 自定义安装程序，支持选择安装路径、创建快捷方式、写入卸载信息。
- beta/prod 双通道构建和发布，自动生成 `latest.json`。

## 技术栈

- .NET 8
- WinUI 3 / Windows App SDK
- AWS SDK for .NET S3
- SSH.NET
- WinForms 自定义安装器
- PowerShell 自动构建脚本

主程序目标框架：

```text
net8.0-windows10.0.19041.0
```

最低 Windows 目标平台：

```text
10.0.17763.0
```

## 项目结构

```text
ROS-APP/        主程序源码，WinUI 3 桌面应用
installer/      自定义 exe 安装器源码
scripts/        构建、打包、发布、beta 同步脚本
icons/          应用图标
docs/           项目文档
CHANGELOG*.txt  更新日志
```

构建产物不会提交到 Git：

```text
dist/
out/
**/bin/
**/obj/
*.log
```

## 核心实现

### 连接层

连接入口在 `ROS-APP/Views/MainPage.xaml.cs` 的 `OnConnectClicked`。

S3 模式使用：

- `BasicAWSCredentials`
- `AmazonS3Config.ServiceURL`
- `ForcePathStyle = true`
- `AmazonS3Client`

SFTP 模式使用：

- `SftpClient`
- 用户名对应 Access Key
- 密码对应 Secret Key

程序会根据界面选择的协议初始化不同客户端，并在连接完成后刷新桶列表。

### 桶列表

S3 桶列表通过 AWS SDK 的 `ListBucketsAsync` 获取。

SFTP 没有桶概念，因此界面将 `/` 作为根目录虚拟桶，后续对象浏览直接基于 SFTP 路径操作。

### 对象浏览

S3 对象列表使用 `ListObjectsV2Async`：

- `Prefix` 表示当前路径前缀
- `Delimiter = "/"` 用于模拟文件夹
- `ContinuationToken` 用于分页

SFTP 对象列表使用 `ListDirectory`：

- 目录项映射为文件夹
- 普通文件映射为对象
- 路径统一使用类 Unix 路径格式

对象浏览会维护一份完整快照，再根据筛选框和排序控件更新界面集合。

### 文件预览

选中文件后，程序会尝试读取对象内容：

- S3 使用 `GetObjectAsync`
- SFTP 使用 `OpenRead`
- 大于约 1 MB 的对象不直接预览，避免界面卡顿和内存压力

预览区会显示对象名称、大小、修改时间和文本内容。

### 上传下载

上传：

- S3 使用 `TransferUtilityUploadRequest`
- SFTP 使用 `UploadFile`
- 上传目标 Key 支持目录前缀，例如 `folder/` 或 `folder/file.txt`

下载：

- S3 使用 `GetObjectAsync` 后流式写入本地文件
- SFTP 使用 `DownloadFile`
- 右键下载和传输页下载都会更新进度条、百分比和状态文字

### 设置与密钥保存

配置文件默认保存在用户本机：

```text
%LocalAppData%\ROS-APP\settings.json
```

可保存：

- 是否使用上次 Key
- 上次 Access Key / Secret Key
- 上次协议和端点
- 主题模式
- 日志偏好

注意：该文件不在仓库目录内，不能提交到 Git。

### 更新检测

更新检测入口在“关于”页。

当前正式版更新源固定为：

```text
http://ros.yuban.cloud/ros/latest.json
```

客户端会读取 `latest.json`，比较远端 `version` 与当前程序集版本。发现新版本后，界面启用下载按钮，调用系统浏览器打开 `downloadUrl`。

`latest.json` 标准格式：

```json
{
  "version": "0.0.48",
  "downloadUrl": "http://ros.yuban.cloud/ros/ROS-APP-Setup-0.0.48.exe",
  "notes": "更新说明",
  "sha256": "文件 SHA256",
  "buildTime": "2026-02-23T16:19:46+08:00"
}
```

更详细的更新发布说明见：

```text
docs/更新检测与发布说明.md
```

## 构建

先确保安装：

- Windows 10/11
- .NET 8 SDK
- Windows App SDK 相关构建组件

还原并构建：

```powershell
dotnet restore ROS-APP/ROS-APP.csproj
dotnet build ROS-APP/ROS-APP.csproj -c Release -r win-x64
```

## 打包发布

构建 beta 版本并生成安装包和 `latest.json`：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/auto_build.ps1 -Channel beta -Environment beta -Message "本次 beta 更新说明"
```

构建正式版：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/auto_build.ps1 -Channel prod -Environment prod -Message "本次正式版更新说明"
```

将 beta 同步为正式版：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/sync_beta_to_prod.ps1 -Version 0.0.48
```

输出目录：

```text
dist/beta/      beta 程序目录
dist/prod/      prod 程序目录
out/beta/       beta 安装包与 latest.json
out/prod/       prod 安装包与 latest.json
```

## 安装器实现

`installer/` 是一个独立 WinForms 安装器项目。

打包流程会先将主程序发布目录压缩为 `payload.zip`，再作为嵌入资源编译进安装器。安装器运行后会：

- 显示安装界面
- 支持自定义安装路径
- 解压主程序文件
- 创建桌面和开始菜单快捷方式
- 写入卸载脚本
- 写入当前用户注册表卸载信息

## 开源注意事项

不要提交以下内容：

- `dist/`
- `out/`
- `bin/`
- `obj/`
- `*.log`
- 本机 `%LocalAppData%\ROS-APP\settings.json`

源码中不应写入真实 Access Key / Secret Key。密钥只应由用户在运行时输入或保存在本机配置文件中。

