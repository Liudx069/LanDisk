# 构建与发布说明

本文件说明如何从源码构建 LanDisk，以及如何生成 `dist/delivery/` 下的交付产物。

## 1. 构建环境要求（仅开发机需要）

| 组件 | 本任务实际使用的版本 | 说明 |
|---|---|---|
| 操作系统 | Windows 10 专业版 19045（64 位） | 应用本身依赖 Windows 文件系统与卷管理 API |
| .NET SDK | 10.0.400 | 目标框架 `net10.0-windows`，LTS |
| ASP.NET Core 运行时 | 10.0.11 | 随 SDK 提供 |
| Node.js | v24.19.0（`npm.cmd` 11.17.0） | **仅用于构建前端**，发布后运行不需要 Node.js |
| PowerShell | pwsh 7 | 运行构建脚本 |

运行发布包的用户电脑**不需要**安装 Node.js、Python、DSH 或任何数据库服务。

## 2. 一键构建

```powershell
# 在仓库根目录执行
pwsh -File tools/Build-All.ps1
```

该脚本依次执行：

1. `npm ci`（或首次的 `npm install`）+ `npm run build`，把前端构建到 `src/LanDisk.Server/wwwroot/`；
2. `dotnet test`，运行全部自动化测试；
3. `dotnet publish src/LanDisk.Server -c Release -r win-x64 --self-contained true`，输出自包含运行目录；
4. 打包 `dist/delivery/LanDisk-win-x64.zip`、源码包与 SHA256 清单。

## 3. 手动分步构建

```powershell
# 3.1 前端（仅开发期需要）
cd src/LanDisk.Web
npm install
npm run build          # 输出到 ../LanDisk.Server/wwwroot

# 3.2 自动化测试
cd ../..
dotnet test tests/LanDisk.Tests/LanDisk.Tests.csproj

# 3.3 发布 win-x64 自包含运行包
dotnet publish src/LanDisk.Server/LanDisk.Server.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -o dist/publish/LanDisk-win-x64
```

`PublishSingleFile` 保持关闭：自包含目录运行包可以完整保留原生库、静态资源和证书相关组件，启动更稳定，也便于排查问题。

## 4. 交付目录结构

```
dist/
  publish/LanDisk-win-x64/      # 自包含运行目录（可直接运行 LanDisk.Server.exe）
  delivery/
    LanDisk-win-x64.zip         # Windows 运行包
    LanDisk-source.zip          # 源码包
    SHA256SUMS.txt              # 全部交付文件的 SHA256
    docs/                       # 中文使用说明、构建说明、测试报告、已知限制、第三方许可
```

## 5. 依赖与许可证

见 `docs/THIRD_PARTY_LICENSES.md`。后端只使用 .NET 10 自带组件，没有引入 NuGet 第三方包；
前端运行时依赖 React 19、react-dom、qrcode、hash-wasm，全部在构建期打包进本地静态资源，
运行时不访问任何 CDN。

## 6. 版本锁定

`src/LanDisk.Web/package.json` 中的依赖使用精确版本号（无 `^`/`~`），
`npm install` 会生成 `package-lock.json` 固化完整依赖树。
后端依赖由 SDK 版本决定。

## 7. 测试数据

`tests/samples/` 中的合成媒体由 ffmpeg 生成，命令记录在 `docs/TEST_PLAN.md`。
这些文件仅用于测试，不包含任何用户真实数据。

---

## 8. 任务 03 新增的构建目标

发布目录 `dist/publish/LanDisk-win-x64/` 现在由 **三次发布** 组成，脚本顺序固定：

```powershell
# 1) 桌面外壳 —— 产出 LanDisk.exe
dotnet publish src/LanDisk.App/LanDisk.App.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=embedded -o dist/publish/LanDisk-win-x64

# 2) 无界面服务端 —— 产出 LanDisk.Server.exe（与外壳同目录）
dotnet publish src/LanDisk.Server/LanDisk.Server.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=embedded -o dist/publish/LanDisk-win-x64

# 3) 更新程序 —— 单文件自包含，放到 updater\ 子目录
dotnet publish src/LanDisk.Updater/LanDisk.Updater.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o dist/publish/LanDisk-win-x64/updater
```

三者都是同一 TFM（`net10.0-windows`）的 `win-x64` 自包含发布，共享的运行时文件完全一致，
因此先发布外壳、再发布服务端到同一目录是安全的（后一次只是补上自己的 apphost 与同名运行时文件）。

**为什么更新程序是单文件**：更新前服务端会把它**复制到安装目录之外**
（`%LOCALAPPDATA%\LanDisk\updates\runner\<随机>\`）再启动，因为接下来它要替换的就是安装目录里的内容。
单文件让这一步只是一次文件拷贝。

### 8.1 安装清单 `install-manifest.json`

发布完成后脚本会在发布目录写入 `install-manifest.json`，记录**本次安装包含哪些文件**：

- 它的作用是让更新与回滚**只删除自己装过的文件**，用户自己放进程序目录的东西永远不在删除范围内；
- 它**不是发布内容**：`tools/Build-UpdatePackage.ps1` / `keytool pack` 会把它排除在 `.lupd` 之外，
  服务端也会拒绝任何声明了它的更新包（`RESERVED_PATH`），因为它是"安装了什么"的记录，
  只能由安装/更新过程生成。

**生成顺序是有语义的**（06 轮修复的缺陷正出在这里）：

```
1) 写 build-info.json          ← 运行包对自己的身份声明
2) 枚举发布目录的最终文件       ← 此时 build-info.json 已经存在，才会被枚举到
3) 写 install-manifest.json    ← files 清单 + buildInfo { path, bytes, sha256 }
```

先枚举、后写 `build-info.json` 会让 release ZIP 里存在该文件、而清单的 `files` 里永远没有它。
`tools/Build-All.ps1` 除了顺序之外还在写完后立即断言 `files` 含有 `build-info.json`。

`install-manifest.json` 中的 `buildInfo` 记录发布者声明的身份文件：

```json
"buildInfo": { "path": "build-info.json", "bytes": 227, "sha256": "…" }
```

这三个值必须与 release ZIP 及签名 `.lupd` payload 内的真实 `build-info.json` **完全一致**。
`tools/Verify-InstallManifest.ps1` 会打开最终产物复算并逐项比对，任一项不符即以非 0 退出，
`tools/Build-All.ps1` 据此中止，**不会继续生成正式候选**：

```powershell
pwsh -File tools/Verify-InstallManifest.ps1
# INSTALL_MANIFEST_CONTAINS_BUILD_INFO = PASS
# 证据：dist/delivery/INSTALL_MANIFEST_VERIFY.json
```

更新时 `UpdateApplier` 会用磁盘上的真实字节刷新这条记录，因此安装目录不会一直声明旧版本的
`build-info.json`；回滚时程序目录整体从备份恢复，记录与被它描述的文件一起回到旧值。

### 8.2 发布签名密钥

```powershell
pwsh -File tools/New-SigningKey.ps1                    # 生成到 %LOCALAPPDATA%\LanDisk-ReleaseSigning\
pwsh -File tools/New-SigningKey.ps1 -Path D:\keys\release.pem
```

脚本会：

- 生成 ECDSA P-256 私钥（PKCS#8 PEM），并尽量用 `icacls` 收紧为仅当前用户可读写；
- **拒绝**把私钥写到仓库内部；
- 已存在时拒绝覆盖（除非显式 `-Force`，且会先备份原文件）；
- 打印出需要粘贴进 `src/LanDisk.Update/ReleasePublicKey.cs` 的 `KeyId` 与 Base64 公钥。

打包时私钥的解析顺序：`-KeyPath` → `$env:LANDISK_SIGNING_KEY` → 默认离线位置。
**没有私钥时不会产出"未签名的包"**——脚本直接失败，因为没有签名的包任何安装都不会接受。

```powershell
pwsh -File tools/Build-UpdatePackage.ps1 `
    -PayloadDir dist/publish/LanDisk-win-x64 -Version 1.2.0 -MinimumFrom 1.0.0 `
    -NotesFile dist/release-notes.md
```

`tools/Build-All.ps1` 会自动调用它，并**立刻用 `keytool verify` 回读校验刚生成的包**
（打包工具自己报告成功不算数，只有能通过 `Extract` 的签名与逐文件哈希校验才算）。

### 8.3 安装程序 LanDisk-Setup.exe

安装程序是 `src/LanDisk.Setup`（WinForms，单文件自包含）。它**内嵌**上面那个 release ZIP，
并在程序集元数据里记录该 ZIP 的 SHA256：

```powershell
dotnet publish src/LanDisk.Setup/LanDisk.Setup.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:LanDiskPayloadZip=dist/delivery/LanDisk-win-x64.zip `
    -p:LanDiskPayloadSha256=<release ZIP 的 SHA256> `
    -o dist/publish/LanDisk-Setup-win-x64
```

`tools/Build-All.ps1` 会自动完成这一步，并写出 `dist/delivery/setup-info.json`（记录安装程序哈希
与内嵌载荷哈希）。`tools/Verify-CandidateIdentity.ps1` 的 `CAND-15` 会把三者对齐核对：
安装程序实际哈希 == 记录值，且记录的载荷哈希 == release ZIP 实际哈希。

没有 `-p:LanDiskPayloadZip` 时也能编译出一个安装器，但它**没有载荷**，运行时会明确拒绝安装，
而不是装出一个空目录。

防火墙规则可以在不接触防火墙、也不需要管理员权限的情况下审计：

```powershell
dist/delivery/LanDisk-Setup.exe --print-firewall-plan
# 逐条打印 netsh 参数，并以 INVARIANTS: PASS / FAIL 结束
```

安装/修复/接管/单实例/卸载的真实验收：

```powershell
pwsh -File tools/Setup-Acceptance.ps1
```

它一律安装到**任务自建的临时目录**并带 `--no-register --no-firewall --no-launch`：
不需要管理员权限，不写真实开始菜单/桌面/「已安装的应用」，也不碰 Windows 防火墙。
需要 UAC 才能验证的"规则真实生效"记为 `NOT_RUN`，不伪造 PASS。

### 8.4 实机升级验收

```powershell
pwsh -File tools/Update-ReleaseVerify.ps1            # 完整：解压 → 启动 → 升级 → 回滚
pwsh -File tools/Update-ReleaseVerify.ps1 -SkipBuild # 复用已有的发布目录
```

脚本要求 **8891 / 8892 空闲**（它就在这两个固定端口上验收）。占用时报告 `BLOCKED` 并退出码 2，
不会去改端口，也不会杀掉占用端口的程序。
