# 测试方案与复现说明

本文件说明如何复现本任务的自动化测试、发布包冒烟验收、真实浏览器验证，
以及哪些验收项在当前环境无法执行（NOT_RUN）及如何在有设备时补做。

---

## 1. 自动化测试（xUnit）

```powershell
# 全部用例
dotnet test tests/LanDisk.Tests/LanDisk.Tests.csproj

# 只跑某一组（用例 ID 前缀即验收编号）
dotnet test tests/LanDisk.Tests/LanDisk.Tests.csproj --filter "FullyQualifiedName~PathSafetyTests"
dotnet test tests/LanDisk.Tests/LanDisk.Tests.csproj --filter "FullyQualifiedName~UploadTests"
```

**设计原则**

- 每个测试用例都会启动一个**独立**的 LanDisk 实例：独立数据目录、独立沙箱目录、独立回环端口（`0` = 自动分配）。
- 所有文件操作都发生在 `<仓库>/.testrun/<用例名-随机>/` 之内，**不触碰任何用户真实目录**。
- 每个用例使用不同的浏览器 cookie 容器，模拟不同的设备身份。
- 自动化测试**只绑定回环地址**，不会向局域网暴露端口。
- 涉及网络的策略（LAN 放行、CIDR、公网来源拒绝）用合成连接（`DefaultHttpContext` + 伪造的
  `RemoteIpAddress`）驱动中间件验证，因此不需要真的打开 LAN 端口。

**用例与验收项对应**

| 测试文件 | 覆盖的验收项 |
|---|---|
| `PathSafetyTests.cs` | F04、F05、F06 |
| `ListingTests.cs` | F01、F02 |
| `PreviewTests.cs` | F03 |
| `MediaAndDownloadTests.cs` | M01（HTTP 层）、M02、M04、D01、D02 |
| `UploadTests.cs` | U01–U07 |
| `RenameTests.cs` | R01 |
| `RecycleTests.cs` | R02、R03、R04 |
| `AuthTests.cs` | A01–A06、E03 |
| `NetworkTests.cs`（`VolumeTests`） | V01–V04 |
| `NetworkTests.cs`（`NetworkTests`） | N01–N04 |
| `SecurityHeaderTests.cs` | 响应加固、错误信息脱敏 |
| `SortingTests.cs` | 1.1.0 排序（自然序、大小写、中文/emoji、时间、类型、0B、文件夹优先、10000 项、分页稳定、偏好记忆） |
| `SearchFilterTests.cs` | 1.1.0 搜索与筛选（范围、组合、无结果、权限边界、截断提示、大目录） |
| `LibraryTests.cs` | 1.1.0 最近、收藏、缩略图、存储信息 |
| `BatchTests.cs` | 1.1.0 批量计划、流式 ZIP、部分失败清单、批量删除逐项结果 |
| `AdminAutoEntryTests.cs` | 1.1.0 loopback 自动进入、跨站拒绝、Cookie 属性、令牌不外泄、重启后状态、偏好与收藏持久化 |
| `HeaderFormatTests.cs` | Content-Disposition 的 RFC 5987 与 ASCII 回退 |
| `PerformanceTests.cs` | 缩略图并发内存、10000 项目录首屏/排序/分页/搜索耗时 |

## 2. 大文件上传（U05，默认不运行）

4 GiB 以上的上传测试默认不执行，避免每次常规测试都写入数 GB 数据。

```powershell
$env:LANDISK_BIGFILE_TEST = '1'
dotnet test tests/LanDisk.Tests/LanDisk.Tests.csproj --filter "FullyQualifiedName~U05_file_larger_than_four_gibibytes"
Remove-Item Env:\LANDISK_BIGFILE_TEST
```

需要目标卷有 5 GiB 以上可用空间。用例会记录托管堆与工作集变化。

## 3. 发布包冒烟验收（E02/E03/E04、P01、P03）

```powershell
pwsh -File tools/Release-SmokeTest.ps1 -PublishDir dist/publish/LanDisk-win-x64
```

脚本使用**真正发布出来的 `LanDisk.Server.exe`**（不是 dev server），在回环地址上：

1. 检查全新数据目录中没有共享与设备记录（E03）；
2. 启动进程并确认 `/api/health` 可用（E02/E04）；
3. 确认前端静态资源由发布包自身提供、`index.html` 无内联脚本（P01）；
4. 确认默认只监听回环、匿名访问被拒绝（E03）；
5. 在临时沙箱中真实执行六项操作：查看、Range 播放请求、下载并核对 SHA256、
   上传并核对 SHA256、中文名重命名、删除与恢复（P03）；
6. 发送退出请求，确认进程正常结束、端口已释放（E02）。

现场会保留在 `.releasecheck/<时间戳>/`，内含 `release-smoke.json` 证据与 `server.log`。

## 4. 真实浏览器播放验证（M01/M03/F03/D01 的浏览器侧）

```powershell
# 一次性安装
cd tools/e2e; npm install; npx playwright install chromium webkit; cd ../..

node tools/e2e/playback-test.mjs --server dist/publish/LanDisk-win-x64/LanDisk.Server.exe
```

脚本用 Playwright 的 Chromium 真正打开手机端页面，点击列表中的文件，然后：

- 验证图片解码（`naturalWidth > 0`）；
- 验证文本按纯文本显示、`<b>` 未被解析；
- 验证视频**真实解码播放**并拖动到开头 / 中间 / 结尾后继续播放；
- 验证音频元数据解码；
- 记录 MP4/H.264 在该引擎中的实际表现；
- 验证不兼容容器给出「不支持 + 提供下载」的提示；
- 验证浏览器下载事件与文件大小。

> **边界**：Playwright Chromium 是真实浏览器引擎，但**不是** iPhone Safari，
> 也**不能**用来声称 iPhone 兼容性。请勿把它的结果当作 `IPHONE_REAL_DEVICE` 的结论。

## 4.1 界面自动化（1.1.0 新增）

```powershell
node tools/e2e/ui-test.mjs --engine chromium --server dist/publish/LanDisk-win-x64/LanDisk.Server.exe
node tools/e2e/ui-test.mjs --engine webkit  --server dist/publish/LanDisk-win-x64/LanDisk.Server.exe
```

脚本在临时沙箱中启动发布包、创建共享、完成配对（并把设备提升为可读写），
然后用真实浏览器完成以下检查，每一步都记录实际观察到的值：

| 编号 | 检查 |
|---|---|
| UI-ADMIN | 管理端在 loopback 上自动进入，正常流程没有令牌输入框 |
| UI-ENTRY | 浏览器打开后直接进入文件列表，无令牌输入 |
| UI-MOBILE-NAV | 手机竖屏底部导航可见且有 5 个入口 |
| UI-TOUCH | 所有可点区域高度不低于 40px |
| UI-SORT | 切换为「大小 大→小」后，文件夹仍在前，其后首个文件是最大的文件 |
| UI-GRID / UI-THUMB | 网格视图渲染卡片，并加载图片缩略图 |
| UI-SEARCH | 当前文件夹范围搜不到跨目录结果，切到「整个共享」后能搜到 |
| UI-FILTER | 按「图片」筛选只返回图片 |
| UI-NAVIGATE | 进入文件夹后看到子项 |
| UI-FAVORITE | 收藏后出现在收藏页 |
| UI-RECENT | 最近页有内容，且窗口外的旧文件不出现 |
| UI-RENAME | 界面重命名生效 |
| UI-SELECT | 真实长按（pointer down/up）进入多选 |
| UI-ZIP | 批量下载触发浏览器下载，文件是合法 ZIP |
| UI-ZIP-NAME | 归档文件名保留中文（或退化为可读的 ASCII 名，见已知限制 6.4） |
| UI-UPLOAD | 通过界面上传后文件出现在列表 |
| UI-BATCH-DELETE | 批量删除后从列表消失、出现在回收站、恢复后回到列表 |
| UI-LANDSCAPE | 横屏下工具栏与搜索可用且无横向溢出 |
| UI-DESKTOP / UI-DESKTOP-COLS | 宽屏显示左侧导航、隐藏底部导航、显示四列表头 |
| UI-ADMIN-DEVICES / UI-PAIRING | 管理端设备列表与撤销按钮；配对二维码、6 位短码、令牌只出现在 URL 片段中 |

结果写入 `dist/e2e-ui-chromium.json` 与 `dist/e2e-ui-webkit.json`。

> **边界**：WebKit 引擎同样**不等于** iPhone Safari。真机项保持 `NOT_RUN`。

## 5. 合成测试数据

`tests/samples/` 中的媒体由 ffmpeg 的合成源生成（`testsrc2`、`sine`），不含任何第三方素材或用户数据。

| 文件 | 编码 | 用途 |
|---|---|---|
| `video-h264-aac.mp4` | H.264 Constrained Baseline + AAC-LC，320×240，6 秒 | 真机播放基线样本 |
| `video-vp9-opus.webm` | VP9 + Opus，320×240，6 秒 | Chromium 真实解码/拖动验证 |
| `audio-pcm.wav` | PCM s16le，4 秒 | 无损音频播放验证 |
| `audio-aac.m4a` | AAC-LC，4 秒 | Safari 常见音频格式 |
| `incompatible-mpeg4.mkv` | MPEG-4 Part 2 in Matroska | 浏览器不支持的容器/编码，用于降级提示验证 |
| `image-sample.png` | PNG 640×360 | 图片预览验证 |

复现命令（使用 `imageio-ffmpeg` 提供的 ffmpeg；也可以用任意 ffmpeg 7.x）：

```powershell
$ff = python -c "import imageio_ffmpeg,sys; sys.stdout.write(imageio_ffmpeg.get_ffmpeg_exe())"

& $ff -y -f lavfi -i "testsrc2=size=320x240:rate=15:duration=6" -f lavfi -i "sine=frequency=440:duration=6" `
  -c:v libx264 -profile:v baseline -level 3.0 -pix_fmt yuv420p -g 15 -c:a aac -b:a 64k -movflags +faststart video-h264-aac.mp4

& $ff -y -f lavfi -i "testsrc2=size=320x240:rate=15:duration=6" -f lavfi -i "sine=frequency=660:duration=6" `
  -c:v libvpx-vp9 -b:v 300k -c:a libopus -b:a 64k video-vp9-opus.webm

& $ff -y -f lavfi -i "sine=frequency=440:duration=4" -c:a pcm_s16le audio-pcm.wav
& $ff -y -f lavfi -i "sine=frequency=880:duration=4" -c:a aac -b:a 96k audio-aac.m4a
& $ff -y -f lavfi -i "testsrc2=size=320x240:rate=10:duration=3" -c:v mpeg4 -vtag XVID incompatible-mpeg4.mkv
& $ff -y -f lavfi -i "testsrc2=size=640x360:rate=1:duration=1" -frames:v 1 image-sample.png
```

## 6. 当前环境无法执行的验收项（NOT_RUN）与补做方法

| 验收项 | 为什么 NOT_RUN | 在有设备时如何补做 |
|---|---|---|
| I01 真实 iPhone 六项操作 | 无 iPhone 设备 | 按 `docs/USER_GUIDE.md` 第 12 节的最短清单执行，记录 iOS 版本、机型、网络类型 |
| I02 真实 iPhone 中断行为 | 同上 | 上传/播放中切后台、锁屏、刷新，记录状态显示是否与真实一致 |
| I03 Safari 与主屏幕入口 | 同上 | 分别用 Safari 标签页和「添加到主屏幕」各测一次，注意两者不是同一使用形式 |
| V02/V03 物理移动盘拔插 | 本机没有连接真实 USB 移动硬盘 | 接一块真实移动硬盘，共享其目录后正常弹出/重新插入，确认离线提示、身份校验与恢复 |
| N02 真实 HTTPS 信任 | 无 iPhone 可安装描述文件 | 在 iPhone 上安装 `.cer` 描述文件并打开「证书信任设置」，用 Safari 访问 `https://<IP>:8892/` 确认无证书警告 |
| U05 大文件上传 | 需要 5 GiB 以上可用空间与较长时间 | 设置 `LANDISK_BIGFILE_TEST=1` 后运行（见第 2 节） |
| Windows 11 / arm64 | 本机是 Windows 10 x64 | 在目标机型上运行发布包并重跑冒烟脚本 |

## 7. 结果记录

自动化测试的详细结果、发布包冒烟结果、浏览器验证结果与最终结论，
统一记录在 `dist/delivery/DELIVERY_REPORT.md`。

每个用例的证据包含：用例 ID、候选版本、环境、执行命令、实际结果、期望结果、日志或证据文件路径。

---

## 8. 任务 03 的测试与验收补充

### 8.1 新增的自动化用例（90 个）

| 测试类 | 用例数 | 覆盖内容 |
|---|---|---|
| `AuditFixTests` | 16 | FIX-01 批量 ZIP 选择集再校验（含就地重写、长度/时间变化）、FIX-02 最近视图截断原因 |
| `UpdatePackageTests` | 35 | 更新包格式、规范化签名的语义、篡改与未签名拒绝、恶意路径、保留设备名、分段包含判定、降级与不匹配、暂存目录再校验、内置公钥自检 |
| `UpdateApplyTests` | 13 | 更新计划（只写变化、只删清单声明过的、无清单时不删）、备份范围、应用与逐文件校验、写入失败如实报告、回滚（程序 + 状态元数据）、固定端口与配对数据在升级前后字节一致、备份裁剪、安装清单容错 |
| `UpdateServiceTests` | 15 | 状态文件原子读写与损坏容错、启动对账（含"更新程序仍在运行时不去抢状态文件"）、暂存的接受/拒绝与清理、计划描述、放弃暂存、应用前置条件（未暂存 / 无 updater）、更新程序被复制到安装目录之外 |
| `UpdateApiTests` | 11 | `/api/admin/health` 结构化自检、已配对设备访问更新接口被拒绝、安装需显式确认、无暂存包拒绝安装、上传陌生密钥签名/非 ZIP/空体被拒绝且不留状态 |

### 8.2 发布包实机升级 / 回滚验收（`tools/Update-ReleaseVerify.ps1`）

**不使用开发服务器，也不使用任何测试替身。**

| 编号 | 检查 |
|---|---|
| T03-00 | 固定端口 8891/8892 空闲，发布目录含 `LanDisk.exe` 与 `updater/LanDisk.Updater.exe` |
| T03-01 | 解压后双击启动 `LanDisk.exe`，90 秒内提供 `/api/health`，无控制台窗口 |
| T03-02 | 不带端口参数启动时绑定固定端口 8891/8892 |
| T03-03 | `/api/admin/health` 结构化自检全部通过，内置发布公钥可用 |
| T03-04 | 第二次启动 20 秒内退出（退出码 0），未启动第二个服务 |
| T03-05 | 第一个实例仍在正常运行，端口未被抢占 |
| T03-06 | 用真实发布私钥把 1.3.0 构建打包并自校验通过 |
| T03-07 / 08 | 篡改一个字节的包被拒绝（HTTP 400），且不留下待安装状态 |
| T03-09 / 10 | 1.3.0 包上传校验通过；安装请求被受理并给出更新程序 PID |
| T03-11 | 旧实例退出（退出码 0），程序文件可以安全替换 |
| T03-12 | 服务以 1.3.0 重新启动并自检通过 |
| T03-13 / 14 | `lastOutcome=Committed`、`installedVersion=1.3.0`；存在更新历史与备份目录 |
| T03-15 | 安装清单更新到 1.3.0 |
| T03-16 | 升级后仍绑定固定端口 8891 |
| T03-17 / 17b | 共享配置、配对设备、共享目录中的用户文件保持不变；**升级前配对的手机无需重新配对仍可访问** |
| T03-18 | 一个"启动不起来"但签名合法的 1.4.0 包被接受（只按签名与哈希判断） |
| T03-19 | 新版自检失败后自动回滚，并重新以 1.3.0 提供服务 |
| T03-20 | 状态记录 `lastOutcome=RolledBack` 与具体原因 |
| T03-21 | 程序文件按字节恢复（`LanDisk.dll` 哈希与回滚前一致） |
| T03-22 | 更新程序日志记录了回滚过程 |
| T03-23 / 23b | 回滚后共享配置未变，配对设备会话依然有效 |
| T03-24 | 退出后端口释放 |
| T03-25 | `%LOCALAPPDATA%\LanDisk` 的 9 个基线文件 SHA256 在整轮操作中完全未变 |

执行：

```powershell
pwsh -File tools/Update-ReleaseVerify.ps1
```

证据：`dist/.rcverify/<时间戳>/update-verify.json`（随交付提供为 `evidence/update-verify.json`）。

### 8.3 追加的浏览器界面用例

Chromium 与 WebKit 各新增 2 项：

- `UI-UPDATE-PANEL` —— 「系统更新」面板显示版本、更新包选择器、更新记录与签名说明，
  且**没有暂存包时不显示安装按钮**；
- `UI-UPDATE-PAIR-LINK` —— 桌面外壳托盘菜单使用的 `/admin#pair` 深链直接打开「添加设备」页。

### 8.4 仍未运行的项目

| 项目 | 状态 | 原因与后续做法 |
|---|---|---|
| IPHONE_REAL_DEVICE | NOT_RUN | 本机没有可访问的真实 iPhone。Playwright 的 WebKit **不是** iPhone Safari。需在真机上重跑第 6 节的清单。 |
| PHYSICAL_REMOVABLE_DRIVE | NOT_RUN | 本机当前没有接入物理移动硬盘。需插入真实移动硬盘后重跑共享/下载/上传/拔插用例。 |
| HTTPS_TRUST | NOT_RUN | 没有可信任该自签证书的真实客户端。需按 `USER_GUIDE.md` 在 iPhone 上安装并信任证书后重测。 |
| PLAYBACK M01（MP4/H.264） | NOT_RUN | 本机 Playwright Chromium 构建不含 H.264 解码器；换用带 H.264 的浏览器或真机 Safari 可覆盖。 |

### 8.5 更新相关的故障注入

本任务的验收刻意包含**失败路径**：

1. **篡改一个字节** → 必须被拒绝，且不留下待安装状态；
2. **入口程序可执行但启动不起来**（把 `LanDisk.exe` 换成 `cmd.exe`，签名与哈希都合法）
   → 必须走完"重启 → 自检失败 → 回滚 → 旧版重新自检通过"的完整链路；
3. **写入阶段失败**（第一次实机验收中真实发生过：更新包错误地包含了 `install-manifest.json`，
   写入后被安装程序自己的记录覆盖，校验立刻发现不一致）
   → 必须回滚，并且必须**不**把 `install-manifest.json` 声明为可发布内容。
