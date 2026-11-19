# RELEASE_CANDIDATE_IDENTITY — LAN_DISK_RELEASE_HARDENING_04

本文件说明"最终发布候选"到底是什么，以及如何**自己复核**这个结论。

## 结论

| 项目 | 值 |
|---|---|
| FINAL_HEAD | `d25cb62549d1bda5d0f5074c3c8dea69d9306c28` |
| 工作树 | clean（`git status --porcelain` 无输出） |
| 产品版本 | 1.3.0 |
| release `build-info.json` 的 commit | `d25cb62549d1bda5d0f5074c3c8dea69d9306c28` |
| release 程序集内嵌 `LanDiskBuildCommit` | `d25cb62549d1bda5d0f5074c3c8dea69d9306c28` |
| 更新包清单 `buildCommit` | `d25cb62549d1bda5d0f5074c3c8dea69d9306c28` |
| 源码包 `SOURCE_HEAD.txt` | `d25cb62549d1bda5d0f5074c3c8dea69d9306c28` |
| 逐条断言结果 | **11 / 11 PASS**（`RELEASE_CANDIDATE_IDENTITY.json`） |

## 为什么这次可以核对

03 轮的问题是：更新包里的 `buildCommit` 指向构建时刻的旧 HEAD，而报告按"同一候选"叙述，
两者之间没有任何可执行的联系。

本轮把这条联系变成机制：

1. **提交号从命令行注入**，不由编译器猜测：
   `dotnet publish ... -p:LanDiskBuildCommit=<sha>`，由 `Directory.Build.props` 写成
   `AssemblyMetadataAttribute`，因此每个程序集都携带同一个值；
   没有提供时**不写**该属性，开发构建无法冒充候选。
2. **产品版本同样注入**（`LanDiskProductVersion`，取自各项目的 `$(Version)`）。
   `BuildIdentity` 读取这个值，而不是"碰巧被加载的那个程序集"的版本——这一点在验收中
   真的暴露过问题：`LanDisk.Core` 当时还是 1.1.0，导致运行实例报告了错误的版本号。
3. **release 目录写出 `build-info.json`**，声明该运行包是哪个提交、哪个版本、什么构建标识。
4. **源码包写出 `SOURCE_HEAD.txt`**，声明源码快照对应的提交与工作树状态。
5. **`tools/Verify-CandidateIdentity.ps1`** 把上述全部内容与当前 HEAD 交叉核对，
   并额外核对"更新包 payload 与 release 目录逐文件一致"和"payload 逐文件哈希与签名清单一致"。

## 自己复核的步骤

```powershell
# 1. 工作树与 HEAD
git -C D:\LanDisk status --porcelain        # 期望：无输出
git -C D:\LanDisk rev-parse HEAD            # 期望：d25cb62549d1bda5d0f5074c3c8dea69d9306c28

# 2. 一条命令跑完全部 11 条断言
pwsh -File D:\LanDisk\tools\Verify-CandidateIdentity.ps1
# 期望：候选身份核对：通过 11 / 失败 0

# 3. 手工看运行包声明了什么
Get-Content D:\LanDisk\dist\publish\LanDisk-win-x64\build-info.json

# 4. 手工看更新包声明了什么（需要源码包里的 tools/keytool）
dotnet run --project D:\LanDisk\tools\keytool\KeyTool.csproj -c Release -- `
    inspect D:\LanDisk\dist\delivery\LanDisk-update-1.3.0.lupd
```

## 需要如实说明的一点

交付运行包与"实机升级/回滚验收所使用的那一份"是**同一份内容**，判定依据是哈希，不是时间戳：

- 验收脚本 `tools/Update-ReleaseVerify.ps1` 解压的正是 `dist/publish/LanDisk-win-x64/`；
- `RELEASE/publish-directory-manifest.tsv` 与 `RELEASE/zip-extracted-manifest.tsv`
  在审核包里逐文件比对（差异 0 条）；
- 更新包的 payload 与 release 目录文件集合一致（`CAND-09`），逐文件哈希一致（`CAND-10`）。

但请注意：本轮在验收过程中**修过 6 次代码**（每次都是发现真实问题后修复并重新提交），
每一次修复都**重新构建了全部产物并重跑了验收**。最终报告里的数字全部来自
`d25cb62549d1bda5d0f5074c3c8dea69d9306c28` 这一版构建，没有用旧包拼新报告。

## 未包含私钥

本文件、审核包与任何交付物中都**没有**发布签名私钥。审核包内只有一份**公钥**
（`RELEASE/update-package/release-public-key.pem`），它与
`src/LanDisk.Update/ReleasePublicKey.cs` 中的常量等价，本来就随产品分发。
私钥在发布机上，路径见交付报告第 4 节。
