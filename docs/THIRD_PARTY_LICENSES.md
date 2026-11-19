# 第三方组件与许可证

LanDisk 自身的源码为本任务新编写。运行发布包**不需要**用户安装任何第三方运行时（.NET 运行时已自包含）。
以下列出实际使用到的第三方组件、版本、许可证与用途。

## 1. 后端（.NET）

| 组件 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| Microsoft .NET Runtime / ASP.NET Core | 10.0.11（随 SDK 10.0.400） | MIT | 应用宿主、HTTP 服务器、加密与证书 API |
| System.Drawing.Common | 10.0.11 | MIT | **1.1.0 新增**：在 Windows 上解码图片并生成列表缩略图。仅针对已授权共享内、由已配对设备请求的图片，且受文件大小与像素上限约束。已知风险：GDI+ 是图像解码攻击面；本应用不用于处理不可信来源的图片。 |
| Windows 内核 API（`kernel32.dll`） | 操作系统自带 | 操作系统组件 | `CreateFileW`、`GetFinalPathNameByHandleW`、`GetVolumeNameForVolumeMountPointW`、`SetFileInformationByHandle` 等 |

除了 `System.Drawing.Common`（Windows 专用、MIT）以外，**后端没有引入其它第三方 NuGet 包**。

## 2. 前端（构建期依赖，运行期无外链）

以下组件在构建时被打包进本地静态资源，运行时不访问任何 CDN、在线字体或远程脚本。

| 组件 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| React | 19.2.0 | MIT | UI 框架 |
| react-dom | 19.2.0 | MIT | DOM 渲染 |
| qrcode | 1.5.4 | MIT | 在浏览器本地生成配对二维码（不请求任何在线服务） |
| hash-wasm | 4.12.0 | MIT | 增量 SHA-256（上传分块校验与前缀校验） |

### 开发期依赖（不进入运行包）

| 组件 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| Vite | 7.1.9 | MIT | 前端构建 |
| @vitejs/plugin-react | 5.0.4 | MIT | React 构建插件 |
| TypeScript | 5.9.3 | Apache-2.0 | 类型检查 |
| @types/react / @types/react-dom | 19.2.2 / 19.2.1 | MIT | 类型定义 |
| @types/qrcode | 1.5.5 | MIT | 类型定义 |

## 3. 测试与工具（不进入运行包）

| 组件 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| xunit | 2.9.3 | Apache-2.0 | 单元/集成测试框架 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | 测试运行器 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | 测试宿主 |
| Playwright（Node） | 1.56.1 | Apache-2.0 | 真实浏览器播放/预览验证 |
| Chromium（Playwright 构建 141.0.7390.37） | 141 | BSD-3-Clause 等（见上游） | 浏览器引擎；仅用于测试，不随交付运行包分发 |
| FFmpeg（经 Python 包 `imageio-ffmpeg` 0.6.0 提供的静态构建，v7.1） | 7.1 | LGPL-2.1 / GPL（取决于构建配置，本处仅用于**生成测试样本**） | 生成合成测试媒体 |
| imageio-ffmpeg（Python 包） | 0.6.0 | BSD-2-Clause | 提供 ffmpeg 可执行文件 |

**FFmpeg 没有被集成进 LanDisk，也没有随运行包分发。** 它只在开发机上用于生成
`tests/samples/` 中的合成测试媒体。LanDisk 本身不做转码，运行时不调用 ffmpeg。

## 4. 测试样本

`tests/samples/` 中的媒体文件全部由 ffmpeg 的合成源（`testsrc2`、`sine`）生成，
不包含任何第三方素材、音乐、影像或用户数据。生成命令见 `docs/TEST_PLAN.md`。

## 5. 许可证文本

MIT / Apache-2.0 / BSD 的完整文本可在各组件的上游仓库获取。
本交付包不复制第三方文档全文；如需要，请按上表版本从上游取得。
