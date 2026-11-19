# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第十六段）

保存时间：2026-09-30（第十六段额度用尽前）
项目：`D:\LanDisk`　候选：**1.7.4 @ `a9b06eb6ed0b15744556b31fc156748b93cc27dc`**

---

## 1. 本段完成的两项（有证据）

### 1.1 安装器视觉证据（第 1 项）— 完成
从**冻结的 1.7.4 Setup**（`dist/delivery/LanDisk-Setup.exe`，SHA256
`edbfb1ba67c2d71fcef082008c513bc443b2faf014b29be3222bba87efed2de0`）取得：

- **9 张状态截图**（`--render-ui`，9/9 检查通过）：
  `setup-step1-off`（全灭）、`setup-step2-mid` / `setup-step3` / `setup-step4` / `setup-step5`（过渡中）、
  `setup-step6-ready`（Ready）、`setup-error`（失败）、`setup-step2-done`、`setup-reduce-motion`
- **真实安装录屏**（`dist/tools/InstallerRecorder`，UI Automation 按下安装器自己的「安装」按钮）：
  抓帧 59、保留 31、安装器退出码 **0**；动图 1,217,816 字节、**7 个互异帧**、帧延时 16/100 秒、
  `GifVerify` 判定 **ANIMATED**
- 位置：`dist/evidence-11/run-11-1.7.4-20260930-031659/02-installer-capture/`
- 预览捕获（静态截图）与真实安装录屏**分别存放**，未混为一类

### 1.2 B 类：动态端口更新与失败回滚（第 2 项）— 完成
新增 `dist/tools/Verify-DynamicPortUpdate.ps1` 并实跑，**结论 PASS**：

```
宿主 PID 2612；配置端口 0/0；实际绑定 54582/54583；健康 True
上传测试包：HTTP 200
B 类（动态端口更新与回滚）：PASS
  配置端口        : 0/0（保持用户配置值，未写成固定端口）
  实际绑定端口    : 54582/54583
  更新程序重启端口: 54582/54583（与实际绑定一致 = True）
  回滚后版本/端点 : 1.7.4 @ 54582（健康 True）
```

**这正是修复的端到端证明**：宿主实际绑在 54582/54583，更新程序日志里的重启记录用的是
**同一组实际端口**而不是配置里的 0；失败包回滚后，服务以**失败更新之前的实际版本 1.7.4**
回到**同一个端点** 54582。

证据文件：`dist/evidence-11/run-11-1.7.4-20260930-031659/08-https/B-dynamic-port/B-dynamic-port-update.json`

用的测试包是 A 类运行留下的**已签名 1.8.0**（`LanDisk-update-1.8.0.lupd`），
在记录中明确标注**测试用途**：**不代替 A 类**（A 类已用交付的正式 `.lupd` 完成 41/41），
也**不用来**声称"旧宿主的动态端口问题已被追溯修复"。

## 2. 第 3 项：隐私检查 —— **未通过，发现真实问题**

新增 `dist/tools/Test-DeliveryPrivacy.ps1` 并对**实际交付集合**逐项扫描（654 个文件，17 个文本）：
结果 **REVIEW_REQUIRED**，命中 4 处，落在 **2 个文件**上：

| 文件 | 命中 |
| --- | --- |
| `DELIVERY_REPORT.md` | 本机私有地址、适配器厂商字样 |
| `runtime-identity\runtime-identity.json` | 本机私有地址（含另一个私网地址）、完整适配器型号 |

- 夹具断言成立：桌面矩阵 `WLAN12-01_NATIVE_WLAN_CALLS = 0`、`WLAN12-02_FIXTURE_SENTINEL_SEEN = PASS`
  —— 截图来源本身是干净的，**但这不足以让整个交付集合通过**。
- 核验身份保留：报告里版本 `1.7.4` ✓、提交 `a9b06eb` ✓；但 **`ARTIFACT_HASHES.txt` 缺失**。

**结论：在把这两处脱敏、并补上 `ARTIFACT_HASHES.txt` 之前，不得宣布隐私通过，也不得进入正式封包。**
（上一段的外置隐私说明里已声明"素材产生后对实际交付集合执行检查，之前不宣布 PASS"，此处正是执行它。）

## 3. 第 4 项：纳入 Full 与正式封包 —— **未完成**

原因有二，均为真实阻断：

1. 第 3 项隐私检查未通过（2 个交付文件含真实网络标识）。
2. `ARTIFACT_HASHES.txt` 缺失（八份独立文件之一）。

此外，Full 判定目前**尚未把安装器证据与 B 类证据纳入必需项** —— 需要扩展
`tools/EvidenceVerdict.ps1` 的 `Scope=Full` 必需清单，加入：

- `02-installer-capture/setup-ui-index.txt` 与录屏动图（并核 Setup SHA256）
- `08-https/B-dynamic-port/B-dynamic-port-update.json`（要求 `passed = true`）
- `08-https/https-e2e-matrix.json`（Matrix 4/4）
- `PRIVACY_CHECK.json`（要求 verdict 为 PASS）

## 4. 本段未触碰边界（遵守情况）

- **未重新编译**：四个冻结产物的字节未变，候选身份仍为 `a9b06eb`。
- **未清空交付目录**：`Build-All.ps1` 的整目录删除已修复；本段未运行 Build-All。
- **1.7.3 原件与失败证据**：原包原件已在上一段被误删（已修复并重建副本 `dce206a0…`，
  说明见 `docs/LAN_DISK_11_173_BUNDLE_PRIVACY_NOTICE.md` 第 0 节）；本段未再改动任何历史材料。

## 5. 续跑第一件事

1. **脱敏两处**：
   - `DELIVERY_REPORT.md` 中引用本机地址与适配器型号的段落（`tools/Generate-DeliveryReport.ps1`
     与 `dist/tools/New-VerifiedReports.ps1` 的模板需改为写"已脱敏"或占位说明，而不是真实值）；
   - `runtime-identity\runtime-identity.json`（`dist/tools/New-RuntimeIdentity.ps1` 输出前对
     `lanAddresses` / 适配器名做遮蔽，保留 version / commit / 端口 / 证书指纹等核验信息）。
   遮蔽**只针对真实网络标识**，不得抹掉候选核验所需的版本、提交、产物哈希与测试身份。
2. **补 `ARTIFACT_HASHES.txt`**（`dist/tools/New-EvidenceInventory.ps1` 已能生成）。
3. **扩展 Full 必需清单**（见第 3 节的四项），再跑隐私检查直到 PASS。
4. 以 `-DeliveryDir dist\delivery` 重跑 Full → 写 `DELIVERY_READY.txt` → 生成正式报告与
   约定的八份独立文件 → 封审核包（Manifest / CRC / 成员集合 / 大小 / 完整 SHA256 + 外置 sidecar）。

**允许在同一批冻结产物上补齐证据后重新判定与 Finalize**；若脱敏只改报告与证据文本、
不触碰产品源码，则产物字节不变、候选身份无需更新。
