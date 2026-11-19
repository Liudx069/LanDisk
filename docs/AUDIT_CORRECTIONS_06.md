# AUDIT_CORRECTIONS_06 — `install-manifest.json` 漏记 `build-info.json`

本文件对应 `LAN_DISK_INSTALL_MANIFEST_FINAL_FIX_06`。它只记录**一处**发布链缺陷的成因与修法，
以及修法为什么必然落在发布脚本与安装清单上，而不是产品功能上。

04 与 05 轮的报告（`docs/AUDIT_CORRECTIONS_04.md`、`docs/AUDIT_CORRECTIONS_05.md`、
`docs/DELIVERY_REPORT.md`）保持原样，本轮未作任何修改。

---

## PKG06-01 — 生成顺序：清单先枚举，身份文件后落地

### 问题（05 实测）

05 的最终产物内部自相矛盾：

| 位置 | `build-info.json` | `install-manifest.json` 的 `files` 是否收录它 |
|---|---|---|
| `dist/delivery/LanDisk-win-x64.zip` | 存在（`LanDisk-win-x64/build-info.json`） | **否** |
| `dist/delivery/LanDisk-update-1.3.0.lupd` | 存在（`payload/build-info.json`，已签名） | —（`install-manifest.json` 本身不在包内，正确） |

也就是说：文件确实被发布了、也确实被签名保护了，但安装清单——那份"本次安装了哪些文件"的记录——
对它一无所知。实测证据（修复前，直接从 05 的 ZIP 内读出）：

```
LanDisk-win-x64/build-info.json      version 1.3.0
LanDisk-win-x64/install-manifest.json  files[] 共 381 项，不含 build-info.json
```

### 成因

`tools/Build-All.ps1` 里的生成顺序是：

```
1) 发布三个可执行程序到 dist/publish/LanDisk-win-x64/
2) Get-ChildItem 枚举目录 → 写 install-manifest.json
3) 写 build-info.json            ← 太晚了
```

第 2 步枚举时 `build-info.json` 还不存在，所以无论枚举逻辑多么正确，清单都不可能包含它。
这不是"忘了加一行"，而是**顺序错误**：清单是对目录的一次快照，快照必须发生在所有被快照的文件
落地**之后**。

05 轮把 `build-info.json` 从"发布元数据"改成了受签名的普通 payload 文件，`.lupd` 侧因此完全正确；
但发布目录侧的清单没有跟着改，于是同一份事实在两个地方说法不一致。

### 修法

**顺序 + 断言 + 独立复核**，三件事缺一不可。

1. **顺序**：`tools/Build-All.ps1` 改为

   ```
   1) 写 build-info.json
   2) 枚举发布目录的最终文件
   3) 写 install-manifest.json（files + buildInfo）
   ```

   脚本在写完之后立即断言 `files` 含有 `build-info.json`，顺序被改坏时**当场抛错**。

2. **记录可核对的值**：`install-manifest.json` 增加一段 `buildInfo`：

   ```json
   "buildInfo": { "path": "build-info.json", "bytes": 227, "sha256": "…" }
   ```

   `files` 只说明"目录里有这个文件名"，`buildInfo` 才说明"这个文件的内容是什么"。
   没有它，"清单收录了 build-info.json"仍然只是一句话，无法证伪。

3. **独立复核（硬门禁）**：新增 `tools/Verify-InstallManifest.ps1`。它不看构建过程的任何中间变量，
   而是重新打开三个最终产物——发布目录、`LanDisk-win-x64.zip`、签名 `.lupd`——复算
   `build-info.json` 的真实字节数与 SHA256，再与清单里记录的值逐项比对：

   | 检查 | 内容 |
   |---|---|
   | `IMCB-03` | `install-manifest.files` 明确包含 `build-info.json` |
   | `IMCB-05` | `install-manifest.buildInfo.path == "build-info.json"` |
   | `IMCB-06/07` | 记录的 `bytes` / `sha256` == 发布目录真实值 |
   | `IMCB-10/11` | 记录的 `bytes` / `sha256` == **release ZIP 内**真实值 |
   | `IMCB-13/14` | 记录的 `bytes` / `sha256` == **`.lupd` payload 内**真实值 |
   | `IMCB-15` | 签名更新包内**不含** `install-manifest.json` |

   脚本输出顶层结论 `INSTALL_MANIFEST_CONTAINS_BUILD_INFO = PASS / FAIL`，
   失败时以非 0 退出；`tools/Build-All.ps1` 在**锁定 FINAL_HEAD 之前**调用它，
   因此"核对失败仍然产出正式候选"在流程上不可能发生。

4. **让声明在更新后依然为真**：`InstallManifest` 新增可选字段 `buildInfo`，
   `UpdateApplier.Apply` 在写完新清单前用**磁盘上的真实字节**重新捕获该记录，
   所以升级后的安装不会继续声明旧版本的 `build-info.json`；
   回滚时程序目录整体从备份恢复，记录与被描述的文件一起回到旧值，无需额外逻辑。

### 为什么这不是"扩大更新格式设计"

- `.lupd` 的清单（`update-manifest.json`，schema 2）**一个字节都没有改**，
  签名语义、字段集合、canonical JSON 全部保持原样；
- 改动只落在 `install-manifest.json`（安装器生成的保留元数据，本来就不进 payload），
  以及发布脚本的生成顺序上；
- `buildInfo` 在读取侧是可选的：改动前安装的目录里没有这个字段，读取时返回 `null`，
  不会被当成损坏（有测试固定这一行为）。

### 与其他轮次的关系

- 05 的 `PKG05-03`（`build-info.json` 进入签名 payload）**继续成立**，本轮未回退：
  `.lupd` payload 仍然包含它、仍然受签名保护，成功更新后它变成目标版本，回滚后恢复旧版本。
- 05 的 `PKG05-01`（候选身份自引用）**继续成立**：本轮的报告同样只在 FINAL_HEAD 锁定之后、
  写入被 `.gitignore` 忽略的 `dist/`，生成报告不改变 HEAD。
- 05 的 `PKG05-02`（源码包完整性）**继续成立**：源码包仍由 `git archive` 生成并逐 tracked blob 校验。
