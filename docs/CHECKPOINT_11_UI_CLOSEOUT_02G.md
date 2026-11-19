# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第七段）

保存时间：2026-09-30（第七段额度用尽前）
项目：`D:\LanDisk`　目标版本 1.7.4　**未升版、未构建正式产物**

---

## 1. 现状

| | |
| --- | --- |
| HEAD | `208f168ee6b44bf737e329410b34625da3a93f55` |
| 本段起点 | `8ae575fc41c6780e8404b79723b18c98f28994ec` |
| 工作树 | clean |
| Chromium e2e | **40 / 40 PASS**（原 39 项 + 本段新增 MOBILE12-11），失败 0 |
| 1.7.3 四产物 / 原失败 TSV / 五张截图 | 未改写 |

## 2. 本段完成项：手机权限自动更新（第二节）

### 2.1 核对结论：原有证据缺了哪一段
`MOBILE12-03/04` 使用 **测试端** `context.request.get('/api/me')` 取回权限。它证明的是
**服务器**改了权限、写入被允许/拒绝 —— 但**不能**证明手机页面自己察觉并刷新。
链路第 2/3/4/6 段全部缺失：
- 2 页面因 heartbeat 修订号变化**自动**请求 `/api/me`；
- 3 返回的权限被当前页面消费；
- 4 操作按钮**自动**更新；
- 6 降权后页面**自动**禁用。

### 2.2 新增 `MOBILE12-11`（只观察页面自身流量）
- 用 `page.on('request')` / `page.on('response')` 记录**页面自发**请求：
  heartbeat 响应里的 `permissionRevision`，以及页面发出的 `/api/me`。
- 测试端**不**调用 `/api/me`、**不**改前端状态、**不**调用刷新函数。
- 流程：同一会话、同一页面、保持前台、不重新加载 ——
  管理端改读写 → 页面自身 heartbeat 返回新修订号 → 页面自行请求 `/api/me` → 上传按钮自动可用；
  再降只读 → 修订号再跳 → 页面再次自行请求 `/api/me` → 上传按钮自动禁用。

实测记录（`dist/e2e-ui-chromium.json`，同时已复制到 `dist/evidence-11/<run>/07-browser/`）：

```
页面自行刷新权限（测试端从不调用 /api/me）：
管理端 17:45:52 改为读写后，页面自己的 heartbeat 返回修订号 5，
页面随即自行请求 /api/me（已观察到），上传按钮自动变为可用=true；
17:45:56 降为只读后，heartbeat 返回修订号 6，页面再次自行请求 /api/me（已观察到），
上传按钮自动禁用=true；
期间未重新加载页面=true，未发起任何配对/设备请求=true
（页面自发的 /api/me 请求共 2 次，全部来自页面而非测试端）
```

### 2.3 一次判为 FAIL 的原因（我这边，非产品）
第一次运行 MOBILE12-11 判 FAIL：heartbeat **每一拍都带修订号**，我却拿"第一个带数字的
heartbeat"当变化信号，于是匹配不上。改为以**修订号相对上一拍的跳变**为信号后通过。
产品逻辑本身正确（`src/LanDisk.Web/src/pages/DeviceApp.tsx:294-302`：
修订号变化时重读 `/api/me` 并重载列表）。已保留该失败记录与修正过程。

## 3. 剩余项

| # | 项目 | 备注 |
| --- | --- | --- |
| 1 | WebKit 复跑含 MOBILE12-11 | 本段只跑了 Chromium；正式验收两端都要 |
| 2 | HTTPS 隔离验证 | 更新前/后/回滚后的监听地址、实际端口、开关、进程身份；确认宿主传的是配置值还是实际绑定值 |
| 3 | Full 判定接入报告与封包 | `matrixReduced=false`、不启用 `SkipMatrixCompleteness`；必需失败或缺证即 BLOCKED，不生成正式 READY/审核包 |
| 4 | 隐私与历史说明 | 1.7.3 原包不改写 + 外置说明（含真实 SSID）；1.7.4 脱敏（截图/录屏/诊断文本/文件名/元数据） |
| 5 | 升版 1.7.4 → 冻结 → 四产物 → 完整验收 → 正式报告/绑定表 → 封包 | 必须先把 1–4 做完 |

## 4. 续跑第一件事

HTTPS 隔离验证（第三节第 1 项）：按 `src/LanDisk.Server/LanDiskHost.cs` 与
`src/LanDisk.Updater/Program.cs` 的**实际**语义，在隔离环境记录更新前、更新后、回滚后的
监听地址 / 实际端口 / `HttpsEnabled` 开关 / 进程身份与健康检查结果；
确认局域网 HTTPS 关闭后不会意外新增局域网监听；确认动态端口 0 未被当作"关闭"；
确认宿主传给更新程序的是**实际绑定端口**而不是配置值。仅发现真实缺陷时最小修复。

## 5. 已知语义（本段沿用，未重复核对）

- `HttpsEnabled`（settings 里的 bool）才是"是否启用 HTTPS"的显式开关；`HttpsPort` 只是端口号。
- `LanDiskHost.cs` 的**回环 HTTPS 监听是无条件的**；`HttpsEnabled` 只控制局域网侧。
- `HttpsPort == 0` 的含义是"挑一个空闲端口"，**不是关闭**。
- `--https-port` 缺失与显式 `0` 在解析上不可区分（都是 0）。
- 更新端口选择已修（`UpdatePortChoice`：显式覆盖 > 宿主实时值 > settings 兜底）。

## 6. 注意

- 诊断/注入构建与证据、`dist/.release-blocking-sandbox`、`dist/.binding-negative-sandbox`、
  `dist/.gate-probe*` **不得**作为 1.7.4 正式验收证据；注入从未进入产品源码。
- `ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保留。
- 正式绑定表必须由最终候选结果重新生成，不得把诊断表改名充数。
- 不改 Owner 真实数据、网络、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准；不触碰其它项目。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
