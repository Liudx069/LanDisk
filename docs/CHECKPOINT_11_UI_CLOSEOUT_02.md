# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 可恢复断点

保存时间：2026-09-29（本轮会话额度用尽前）
项目：`D:\LanDisk`　不得触碰其它项目。

---

## 1. 起点

| | |
| --- | --- |
| 续跑前 HEAD | `347ed76692c1c253b805ddb711013d9ef815b4c8` |
| 历史冻结候选 | `1.7.3@e6bb0f5618b17fc98aaee9b8aad3f4a86461f9c8` |
| 续跑前工作树 | 4 个未提交修改（诊断、夹具、捕获增强），已保全 |

四个 1.7.3 产物、原始失败 TSV、五张失败截图**保持原样，未改写或改标**。

## 2. 当前状态

| | |
| --- | --- |
| 当前 HEAD | `19d4e5943802f07512da06377a914cf0337ae32e` |
| 工作树 | **clean** |
| 单元回归 | **676 / 676 PASS**（无减少） |
| 目标版本 | 1.7.4（**尚未升版、尚未构建产物**） |
| 未提交代码 | 无（全部已提交；另有补丁 `dist/evidence-11/DIAGNOSTIC-uicapture/INFLIGHT-1704-uicapture-isolation.patch`） |

## 3. 已判定：5 个 UI 失败项的定性

分四项分别记录（互不混同）：

| 项 | 结论 |
| --- | --- |
| `CAPTURE_INPUT_ISOLATION` | **缺陷，已修复并已验证**。矩阵原先渲染本机真实 WLAN |
| `ORIGINAL_LAYOUT_TRIGGER` | **UNKNOWN_NOT_REPRODUCED**。触发输入已消失，不强行定性 |
| `DIAGNOSTIC_FIXTURE_MATRIX` | 630/630 PASS，0 行诊断，44677 个文本控件被检查 |
| `FINAL_CANDIDATE_MATRIX` | **尚未产生**（需 1.7.4 冻结后运行） |

关键约束：截图哈希不同只证明输出不同，**不能**单独证明布局失败的根因；输入未隔离也**不能**
等同于原布局断言是误报。

## 4. 已完成的工作（均已提交）

### 4.1 输入隔离
- `ServerController`：捕获模式（`--render-ui`）**一律**注入夹具 WLAN，**漏传开关也不回落**到真实服务；
  `--wlan-fixture` 出现在非捕获启动时直接抛错。
- `CaptureEnvironment`：捕获模式自动带 `--wlan-fixture`。
- `FakeWlanService`：哨兵网络 `LanDisk-夹具哨兵-9F3AC2`、中文、超长、隐藏、空名、仅不可换行空格、
  仅零宽空格、仅 Hangul 填充符、已/未连接、已知/未知、支持与不支持的安全类型。
- `NativeWlanService`：进程级调用计数 `NativeWlanService.Calls`。

### 4.2 可执行验证（`UI_CAPTURE_ENVIRONMENT.tsv`）
```
WLAN12-01_NATIVE_WLAN_CALLS        0       矩阵期间零次真实 WLAN 调用
WLAN12-02_FIXTURE_SENTINEL_SEEN    PASS    夹具确实进入 WiFi 页
UI_LAYOUT_TEXTBLOCKS_INSPECTED     44677   0 行诊断 = 查了 4.4 万控件后无失败
UI_LAYOUT_COMBINATIONS_RUN         630
UI_LAYOUT_ASSERTIONS_FAILED        0
```

### 4.3 顺带确证并修掉的真缺陷
1. **断言 FAIL 时捕获仍返回退出码 0**（原先只看缺页/错页/未知标题）。已修。
2. **`UI_CAPTURE_MISSING` 写成空字段**。现显式写 `0`，并补
   `MISSING_COUNT`/`DUPLICATES`/`UNEXPECTED`；期望集合由生成 targets 的同一循环推出。
3. **单行文本高度断言无法诊断**（只打印空白样本）。现输出结构化诊断到
   `UI_LAYOUT_VERIFICATION.tsv`：元素类型/Name/AutomationId/父链/文本来源/转义文本/Unicode 码点/
   字体字号行高/换行裁切/Actual 与 Desired/控件与父容器矩形/Padding/Margin/对齐/裁切/逻辑视口/
   主题/Inlines 计数。

### 4.4 证据判定器与负向测试
- `dist/tools/EvidenceVerdict.ps1`：单一 READY 判定。读全部必需结果而非预选清单；缺页按
  **期望集合 − 实际集合**计算；解析失败/字段缺失/身份不符/夹具未生效/未检查到控件一律阻断。
- `dist/tools/Test-EvidenceAggregator.ps1`：**9/9 PASS** —— 对照组 N0 未修改副本 = READY；
  N1 注入 FAIL、N2 删必需组合、N3 换旧候选、N4 不可计算空字段、N5 重复项冒充覆盖、
  N6 缺必需证据、N7 真实 WLAN 调用、N8 夹具未进页面，**全部阻断 READY**。

## 5. 诊断构建的位置（不得冒充正式证据）

```
dist/publish/LanDisk-win-x64-DIAGNOSTIC-uicapture/        诊断构建（带结构化诊断）
dist/evidence-11/DIAGNOSTIC-uicapture/                    诊断跑（真实 WLAN，尚未隔离）
dist/evidence-11/DIAGNOSTIC-uicapture-fixture/            夹具跑（隔离已生效）
dist/evidence-11/DIAGNOSTIC-uicapture-strict/             严格跑（含环境断言，630/630）
dist/evidence-11/run-11-1.7.3-20260929-164302/06-desktop-ui-STRICT-FIXTURE/
                                                          上者矩阵文件的副本
```
诊断构建**没有**版本号、`LanDiskBuildId=Diagnostic/UICapture*`，与 1.7.3 冻结产物、与将来的
1.7.4 正式产物都不相同。**不得**填入最终候选验收。

## 6. 未完成（续跑从这里开始）

| # | 项目 | 说明 |
| --- | --- | --- |
| 1 | §四.1 需求—证据绑定重写 | REAL11-03/04/05/09 目前仍错挂在 UIE17 / DEVICE12 上；须分别绑定核心值完整性、按钮间距、设备操作区矩形重叠、扩展长内容布局 |
| 2 | §四.3 手机权限自动更新 | 需同一会话记录 heartbeat `permissionRevision` → 浏览器自动请求 `/api/me` → 按钮更新 → 实际写操作被允许/拒绝；不重配对、不手工刷新 |
| 3 | §四.4 更新端口语义 | 核对"未提供端口参数"与"HTTPS 明确关闭"的区别；隔离环境验证更新/回滚不意外新增 HTTPS 监听 |
| 4 | §四.5 安装器 | 复核现有成果并提供全灭/过渡中/Ready/失败截图 + 实际录屏，绑定最终 Setup SHA256；区分预览捕获与真实安装录屏 |
| 5 | 正反例证明 | 证明"正常布局通过"且"真实挤压/重叠/截断仍会失败"（现有 DEVICE12-06 重叠检测、UIE17 溢出检测可用作反例，需做反向验证） |
| 6 | 升版 1.7.4 | 改 `Version`、提交、工作树 clean、冻结构建提交 |
| 7 | 构建四产物 | Setup / win-x64.zip / update-1.7.4.lupd / source.zip |
| 8 | 完整验收 | 676 回归、630 桌面矩阵、Chromium/WebKit 39+、Setup 迁移、更新与回滚（默认与非默认端口、HTTPS 明确关闭）、runtime/source identity、install manifest、隐私 |
| 9 | 八份交付文件 + 审核包 | 含 Manifest/CRC/成员 SHA256/外置 sidecar |
| 10 | 隐私处理 | 1.7.3 原审核包含真实 SSID，需补外置说明"仅供本地历史审计、不再对外分发"；1.7.4 对外包用夹具截图，并检查报告/日志/码点/文件名/录屏/元数据 |

## 7. 续跑注意事项

- 桌面矩阵每次约 8.5 分钟；不要反复整套重跑安装/更新/回滚。
- 冻结后若再改产品代码，必须更新候选身份并重建对应验收，**不得拼接旧结果**。
- 真机 `NOT_RUN` 仅限已批准白名单：iPhone 真机、真实 WiFi 切换、跨显示器、独立 DPI 进程实测、
  真实防火墙写入。**可自动化却漏跑的不得放进白名单。**
- 不得改 Owner 真实数据、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
