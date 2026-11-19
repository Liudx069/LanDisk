# 第三方组件与许可

LanDisk 只使用明确许可、版本固定、可离线还原的第三方组件。本文件是分发时必须随附的声明。

## 运行时依赖

| 组件 | 版本 | 许可 | 用途 | 说明 |
|---|---|---|---|---|
| WPF-UI | 4.3.0 | MIT | Windows 原生界面的 Fluent 控件与主题 | 仅作为控件库引用；未复制其示例程序中的品牌资源、字体或图片 |
| WPF-UI.Tray | 4.3.0 | MIT | 托盘图标控件 | 与 WPF-UI 同作者同版本 |
| QRCoder | 1.6.0 | MIT | 在应用内本地生成配对二维码 | 只用于绘制二维码；不联网、不上传任何内容 |
| System.Drawing.Common | 10.0.11 | MIT | 生成缩略图与托盘图标位图 | 仅 Windows 使用 |

以上组件均以固定版本声明在对应的 `.csproj` 中，不使用浮动版本范围，也不在运行时从网络加载任何资源。

## 开发期依赖（不随产品分发）

| 组件 | 版本 | 许可 | 用途 |
|---|---|---|---|
| xUnit | 2.9.3 | Apache-2.0 | 单元测试 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | 测试运行器 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | 测试宿主 |
| Playwright | 1.56.1 | Apache-2.0 | 浏览器端自动化（开发期） |

## 字体与图标

- 界面字体使用系统自带的 **Microsoft YaHei UI / Segoe UI**，并通过字体回退列表降级；
  仓库不打包任何字体文件。
- 界面图标使用 WPF-UI 内置的 Fluent 图标（`SymbolRegular`），不引入来源不明的图标集。
- 产品图标由 `src/LanDisk.App/AppIcon.cs` 在运行时绘制，仓库不含不透明的二进制素材。

## 许可全文

各组件自带的许可全文位于 NuGet 包内（`%USERPROFILE%\.nuget\packages\<包名>\<版本>\LICENSE.md`），
或见其上游仓库：

- WPF-UI：https://github.com/lepoco/wpfui
- WPF-UI.Tray：https://github.com/lepoco/wpfui
- QRCoder：https://github.com/codebude/QRCoder
- System.Drawing.Common：https://github.com/dotnet/runtime
