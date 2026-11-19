# LanDisk 更新端口缺陷记录（LAN_DISK_11_EXACT_CANDIDATE_EVIDENCE_REPAIR_01 期间发现）

本文记录 1.7.2 精确候选核验过程中发现的**真实产品缺陷**、它的两处成因、修法、证明方式，以及
**它的作用范围与无法回溯修复的部分**。写这份文件的目的是让接手的人不必重走一遍调查。

- 发现于：1.7.2 冻结候选（`9441f97`）的升级验收，**首次在非默认端口上执行**时
- 修复于：1.7.3（`7d80a49` 正向路径、`e6bb0f5` 回滚路径）
- 现状：**已修复并加门禁**；但见第 6 节的回溯限制

---

## 1. 症状

在 18891/18892（非默认端口）上执行升级验收：

```
15:59:42  更新前状态：版本 1.7.1，... 管理端口 18891，局域网端口 18892
15:59:56  已启动 ...\LanDisk.exe（...，HTTP 端口 8891，HTTPS 端口 8892）   ← 回到默认端口
16:00:56  健康检查：未通过 — 由于目标计算机积极拒绝，无法连接 (127.0.0.1:18891)
16:01:18  开始回滚
```

更新程序**知道**真实端口（它自己刚打印出来），却在重启宿主时用了 8891/8892。它随后按被告知的
18891 做健康检查 → 拒绝连接 → 判定新版自检失败 → **自动回滚**。于是升级永远无法完成。

`lastOutcome` 停在 `RecoveryRequired`，安装目录被回滚到 1.7.1（回滚本身是正确的：411 个程序文件
按字节恢复，9 个受管状态文件恢复）。

## 2. 成因（第一处：正向路径）

宿主把**实时绑定的端口**交给了更新程序，这一步的意图是对的：

- `src/LanDisk.Update/UpdateService.cs` 启动更新程序时传 `--http-port` / `--https-port`
- 实测日志里也确实写着 `管理端口 18891，局域网端口 18892`

但更新程序在选择重启端口时，让**磁盘上的 settings.json 盖过了实时值**：

```csharp
// src/LanDisk.Updater/Program.cs（修复前）
int port = portOverride ?? ReadConfiguredPort(options.DataRoot, options.HttpPort);
```

`ReadConfiguredPort(dataRoot, fallback)` 返回 `settings.HttpPort > 0 ? settings.HttpPort : fallback`。
验收的数据目录里根本没有 `settings.json`，于是取到 `AppSettings` 的默认值 8891 —— 实时值 18891 被丢弃。

## 3. 成因（第二处：回滚路径）—— 第一次只修了一半

修好正向路径后重跑，出现了一个**不对称**现象：

```
16:53:45  已启动 ... HTTP 端口 18891，HTTPS 端口 18892   → 健康检查 3.4 秒通过   ✓ 正向已修好
16:56:39  已启动 ... HTTP 端口 8891，HTTPS 端口 18892   → 健康检查失败          ✗ 回滚仍是错的
```

HTTPS 对、HTTP 错 —— 说明回滚分支**单独算了一个 HTTP 端口**。定位到：

```csharp
// src/LanDisk.Updater/Program.cs（修复前，回滚分支）
// The restored settings may name a different port; re-read before health checking.
int port = ReadConfiguredPort(options.DataRoot, options.HttpPort);
int? restartedPid = StartEntryPoint(options, out string startError, port);   // port 作为 portOverride
```

`portOverride` 在 `StartEntryPoint` 内部优先级最高，所以这个值压过了一切。注释的原意是"回滚恢复了
设置，重新读一遍"，但效果是把实时值覆盖掉了。

## 4. 修法

把"该绑哪个端口"写成一个有名字、有测试的决策，两条重启路径都必须用它：

```csharp
// src/LanDisk.Core/State/UpdatePortChoice.cs（新增）
public static int Resolve(int explicitOverride, int liveFromHost, int storedInSettings)
{
    if (explicitOverride > 0) return explicitOverride;
    if (liveFromHost > 0) return liveFromHost;
    return storedInSettings > 0 ? storedInSettings : 0;
}
```

优先级：**显式覆盖 > 宿主实时值 > settings.json 兜底**。理由写进了代码注释：存储值说明产品上次
是怎样被**配置**的，实时值说明它此刻正在哪里**服务**；升级的职责是把服务放回它原来的地方——
去程和回程是同一个要求。

- 正向：`Program.cs` 两处（http 与 https）改用 `UpdatePortChoice.Resolve`
- 回滚：同一函数，不再把存储值当显式覆盖

## 5. 门禁与"门禁本身是否可信"

| 门禁 | 内容 |
| --- | --- |
| `UPDATEPORT12-01` | 缺陷场景本身：实时 18891 / 存储 8891 → 必须得 18891 |
| `UPDATEPORT12-02` | 显式覆盖优先于实时值与存储值 |
| `UPDATEPORT12-03` | 存储值只在没有实时值时兜底；两处都没有时返回 0 而非绑定任意端口 |
| `UPDATEPORT12-04` | HTTPS：宿主无 HTTPS 时传 0，回落存储值是正确行为；不会凭空造出监听端口 |
| `UPDATEPORT12-05` | **源码级**：更新程序只有两条重启路径，所有 `ReadConfiguredPort`/`ReadConfiguredHttpsPort` 的出现都必须是 `UpdatePortChoice.Resolve` 的兜底实参，`Resolve` 调用点恰好 3 个 |

`UPDATEPORT12-05` 存在的理由：单元级的优先级测试**看不见**第二处缺陷 —— 函数是对的，是第二个调用方
没用它。

**已做反向验证**：把回滚那行改回旧写法，门禁立刻失败并指出 `line 332: int port = ReadConfiguredPort(...)`；
改回后 5/5 通过。一个不会失败的门禁等于没有门禁。

## 6. 作用范围（重要，且**不可回溯修复**）

**执行升级的是"已安装的那一份"更新程序，不是新包里的那一份。**
`UpdateService.UpdaterPath => UpdateLayout.FindUpdater(InstallRoot)` —— 从**安装目录**取。

因此：

- **已装 1.7.3 及以后**：升级会正确回到原端口。这是修复真正覆盖的范围。
- **已装 1.7.1 / 1.7.2**：升级到 1.7.3 时跑的仍是**旧的、有缺陷的**更新程序，无法从新版本这一侧
  回溯修复。这个限制是"更新程序随安装存在"这一设计固有的，不是本次修复的疏漏。

**影响面比最初判断的要窄**，这里更正一次过宽的说法：

- 在**管理页里改端口**（产品支持的路径）→ 端口被写进 `settings.json` → 旧更新程序读到的就是正确端口
  → **升级正常**。这是绝大多数用户的情况。
- 只有**实时端口与 `settings.json` 不一致**时才会踩到，例如用命令行 `--port` 启动而设置文件里是别的值。
  验收脚本自己正是这样配置的（它用 `--port` 启动、数据目录里没有 `settings.json`），所以是它把这个
  从没被走过的分支踩了出来。

## 7. 证据位置（均在 `dist/evidence-11/` 下）

| 内容 | 位置 |
| --- | --- |
| 1.7.2 期间的失败现场（非默认端口，旧更新程序） | `run-11-20260929-115018/03-update-rollback/FAIL-nondefault-ports-updater-restarts-on-8891/` |
| 1.7.2 期间"期望提交写成 HEAD"导致的 4 项失败现场 | `.../03-update-rollback/FAIL-expected-commit-was-HEAD/` |
| 1.7.3 期间"旧更新程序"现场 | `.../03-update-rollback/FAIL-1.7.1-updater-runs-old-code/` |
| **只修一半**（正向好、回滚坏）的现场 | `.../03-update-rollback/FINDING-B-rollback-port-half-fixed/` |
| 默认端口完整通过（Owner 的真实路径） | `.../03-update-rollback/PASS-A-default-ports-1.7.1-to-1.7.3/` |

关键日志行都在各自的 `data/updates/updater.log` 里，`update-verify.json` 是逐项结论。

## 8. 怎么复现

```powershell
# 正向 + 回滚，非默认端口（修复后会看到"HTTP 端口 18891，HTTPS 端口 18892"）
pwsh -NoProfile -File tools\Update-ReleaseVerify.ps1 `
  -PublishDir dist\publish\LanDisk-win-x64-1.7.3 `
  -Version 1.7.3 -TargetVersion 1.8.0 -BrokenVersion 1.8.1 `
  -HttpPort 18891 -HttpsPort 18892 `
  -KeyPath "$env:LOCALAPPDATA\LanDisk-ReleaseSigning\release-signing.pem"
```

注意：跑之前必须没有其它 LanDisk 在运行 —— 外壳取一个每用户单实例互斥量，第二个实例会直接退出，
验收会在"启动桌面外壳"这一步失败。本轮就是因此停过一次。

## 9. 顺带记录：本轮修掉的两处补证工具缺陷

1. `tools/Update-ReleaseVerify.ps1` 原先**永远现场重打一个更新包**，于是"升级验收"从来不是关于交付的
   那个包。新增 `-FrozenPackage` 让它能直接应用冻结的正式包，并把被测包的完整 SHA256 记进证据。
2. 同一脚本原先固定用 `git rev-parse HEAD` 当期望提交。当候选提交与仓库尖端不一致（本轮是两个
   只改文档的提交）时，4 项身份核对必然失败。新增 `-ExpectedCommit`。

两处都不是放宽：它们让验收针对**正确的对象**与**正确的身份**，而不是针对脚本当场造出来的替代品。
