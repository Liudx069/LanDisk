# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第十二段）

保存时间：2026-09-30（第十二段额度用尽前）
项目：`D:\LanDisk`　目标版本 1.7.4　**未升版、未构建正式产物**

---

## 1. 现状

| | |
| --- | --- |
| HEAD | `a185da0f86f69830a170ec93e4485e0d01d2410c` |
| 本段起点 | `95a6b46c1e5efece92638ac8352c666029a2e013` |
| 工作树 | clean |
| 单元回归 / Chromium / WebKit | 677/677 · 40/40 · 40/40 |
| 门禁 | 链 7/7 · Full 10/10 · 判定器 9/9 · 绑定 5/5 |
| 1.7.3 四产物 / 原失败 TSV / 五张截图 | 未改写 |

## 2. 本段完成项：历史隐私说明（第二节）

### 2.1 外置说明（包外，原包一字节未改）
交付副本：`dist/delivery/LAN_DISK_11_173_BUNDLE_PRIVACY_NOTICE.md`
纳入版本控制的副本：`docs/LAN_DISK_11_173_BUNDLE_PRIVACY_NOTICE.md`

记录内容：
- 原包文件名与**现场复算的完整 SHA256**：`44b549c64fdd5835090e1d4adb3a4130165ab1f1a8acab72df8bacb6f16428d9`
  （与同目录 sidecar 一致，已复算比对通过），字节数 516,033,414。
- **实际含有什么**（对包内 34 个文本成员逐项扫描的结果）：

| 类别 | 是否出现 | 位置 |
| --- | --- | --- |
| 本机网络适配器的**厂商/型号字样** | **出现** | `01-report/DELIVERY_REPORT.md`、`01-report/EVIDENCE_BINDING_11.tsv`、`01-report/LAN_DISK_11_AUDIT_EXPORT_VERIFIED.md` |
| 本机**私有局域网地址** | **出现**（同一处说明文字） | 同上 |
| 真实无线网络名称（SSID） | **未出现** | — |
| 会渲染真实 SSID 的桌面截图 | **未包含**（包的 `06-desktop-ui/` 只装 TSV） | — |

- 包内 77 张 PNG 与 2 个 GIF **全部来自安装器录制**，不含网络标识。
- 说明中**不重复列出敏感值本身**，只写存在与位置。
- 明确"**外置说明不等于已撤回先前分享**"：不宣称改变外部副本内容或访问权限。

### 2.2 1.7.4 脱敏来源与检查范围（已确定）
**来源**：桌面矩阵在工作台即使用 **WLAN 夹具**，由两条可执行断言保证 ——
`WLAN12-01_NATIVE_WLAN_CALLS = 0`、`WLAN12-02_FIXTURE_SENTINEL_SEEN = PASS`。
因此 1.7.4 的 WiFi 页截图**从一开始就不含真实 SSID**。

**检查范围**（素材产生后逐项执行）：截图、录屏与动图逐帧、日志、诊断文本（含转义文本与 Unicode 码点）、
文件名、报告、源码摘录、元数据。
**必须保留**：版本、提交、产物 SHA256、测试身份（run_id、夹具身份、被测产物哈希）——
脱敏只针对真实网络标识。历史对照如需对外，另做脱敏副本并注明与原件不同、分别记哈希。

在素材尚未产生前**不宣布**最终隐私 PASS（说明里也写明了这一条）。

## 3. 剩余项

| # | 项目 | 备注 |
| --- | --- | --- |
| 1 | HTTPS 端到端脚本 | A/B 分开；覆盖 局域网关闭 / `LanEnabled=true,HttpsEnabled=false` / 正常局域网 HTTPS / 固定与动态端口；记录更新前中后的版本与提交、宿主与更新程序身份、配置端口与实际绑定端口、传给更新程序的参数、实际监听地址、健康检查目标与结果。**冻结前准备好，正式结果在四产物构建后产生** |
| 2 | 升版 1.7.4 → 冻结 → 四产物 → 完整验收 → 正式绑定表 → Full 判定 → 正式报告与封包 | 见第 4 节 |

## 4. 续跑第一件事：直接进入 1.7.4 正式候选阶段

按要求**不再停留在重整断点、重复归档或扩写门禁**。顺序：

1. **准备 HTTPS 端到端脚本**（`dist/tools/Verify-HttpsEndToEnd.ps1`，可用参数化但先不执行）：
   - **A 类**：正常配置的旧版本 → 升级到**冻结的正式 1.7.4 `.lupd`**；
     必须直接使用交付包，记录其**完整 SHA256**，不得现场重打替代包冒充。
   - **B 类**：安装修复后的 1.7.4 宿主，以**动态端口**启动，由它发起更新并验证失败回滚；
     更高版本测试包**明确标注测试用途**，不代替 A、也不声称旧宿主已提前获得修复。
   - 记录维度：版本与构建提交；宿主与更新程序进程身份；配置端口与实际绑定端口；
     宿主传入的更新参数；实际监听地址；健康检查目标与结果。
   - 覆盖：`LanEnabled=false`、`LanEnabled=true,HttpsEnabled=false`、正常局域网 HTTPS、固定与动态端口。
   - 断言要点：局域网 HTTPS 关闭后不得新增物理网卡/通配地址监听；既有回环 HTTPS 保留；
     端口 0 不得被当作关闭；失败回滚恢复到失败更新前的**实际版本与服务端点**（不机械断言"回到 1.7.4"）。
2. **升版 1.7.4**：改各 `csproj` 的 `<Version>` 与 `tools/Build-All.ps1` / `Setup-Acceptance.ps1` /
   `Update-ReleaseVerify.ps1` 的版本默认值 → 提交 → 工作树干净。
3. **冻结构建身份**，构建**四个待验产物**。
4. 对**同一批精确产物**完成全部验收（含重新绑定最终 1.7.4 的 Chromium/WebKit 40 项、
   630 桌面组合、Setup 迁移、正式更新包升级与回滚、HTTPS 端到端、runtime/source identity、
   install manifest、隐私检查）。
5. 生成**正式需求绑定表**（由最终候选结果重新生成，不得把诊断表改名充数）。
6. **Full 判定**通过后生成正式报告并封包。

**允许在同一批冻结产物上补齐证据后重新判定与 Finalize**；不得为再次进入封包流程而无故重新编译、
改变产物哈希使刚完成的证据失效。若确实改了影响发布字节的代码 → 更新候选身份并重建验收，不拼接旧结果。

## 5. 交付清单（保持不变）

四个正式产物：`LanDisk-Setup.exe`、`LanDisk-win-x64.zip`、`LanDisk-update-1.7.4.lupd`、`LanDisk-source.zip`

八份独立文件：`DELIVERY_REPORT_11_VERIFIED.md`、`EVIDENCE_BINDING_11.tsv`、`UI_FAILURES_11.tsv`、
`UI_CAPTURE.tsv`、`UI_LAYOUT_VERIFICATION.tsv`、`EVIDENCE_AGGREGATOR_NEGATIVE_TESTS.json`、
`ARTIFACT_HASHES.txt`、`CODE_REVIEW_11.md`

审核包另含：`UI_PAGE_CATALOG.tsv`、相关原始证据、安装器截图与录屏、历史说明与隐私结果。
重新开包核对 Manifest / CRC / 成员集合 / 大小 / SHA256；最终 ZIP 完整哈希放**外置 sidecar**。

安装器保留现有透明 Logo、单机柜、由上往下点灯与连续过渡，**不重新设计**；
最终代表截图与实际安装录屏绑定**最终 Setup SHA256**。

历史失败、诊断结果、合成对照与正式候选验收**分别标注**。
`ORIGINAL_LAYOUT_TRIGGER` 保持 `UNKNOWN_NOT_REPRODUCED`。Full 未通过时可保留待验产物与失败报告，
但不得生成正式 READY 结论或宣布交付完成，不得绕过现有门禁赶进度。

## 6. 注意

- 诊断/注入构建与证据、`dist/.release-gate-sandbox`、`dist/.https-probe`、
  `dist/.release-blocking-sandbox`、`dist/.binding-negative-sandbox`、`dist/.gate-probe*`
  **不得**作为 1.7.4 正式验收证据；注入从未进入产品源码（引用 0 处）。
- 不改 Owner 真实数据、网络、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准；不触碰其它项目。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
