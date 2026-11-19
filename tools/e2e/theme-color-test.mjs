// 主题颜色编码回归（诊断+回归工具，不参与发布门禁）。
//
// 验证的就是**应用线上运行的那份实现**：src/LanDisk.Web/src/themeColor.mjs 同时被 DeviceApp 与本测试引用，
// 因此不存在"测的和跑的不是同一段代码"。
//
// 覆盖：Owner 点名的四个编码用例 + 真实调色板值 + 非 ARGB 输入必须原样返回。
//
// 用法：node tools/e2e/theme-color-test.mjs

import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '..', '..')
const modulePath = path.join(repoRoot, 'src', 'LanDisk.Web', 'src', 'themeColor.mjs')
const { toCssColor, argbAlpha } = await import(pathToFileURL(modulePath).href)

const cases = [
  // ---- Owner 点名要求的四个编码用例 ----
  { in: '#FFFFFFFF', want: 'rgb(255, 255, 255)', why: '不透明白' },
  { in: '#FF000000', want: 'rgb(0, 0, 0)', why: '不透明黑' },
  { in: '#80FF0000', want: 'rgba(255, 0, 0, 0.502)', why: '50% 红：alpha 必须落在 alpha，不能变成红色通道' },
  { in: '#00FFFFFF', want: 'rgba(255, 255, 255, 0)', why: '全透明白：alpha=0' },

  // ---- 真实调色板值（真机上被错读的那些）----
  { in: '#FFF4F7FB', want: 'rgb(244, 247, 251)', why: 'Arctic window（浅蓝）—— 错读会变浅粉 rgba(255,244,247,0.984)' },
  { in: '#FF0A74C4', want: 'rgb(10, 116, 196)', why: 'Arctic accent（蓝）—— 错读会变品红 rgb(255,10,116)' },
  { in: '#FF17202A', want: 'rgb(23, 32, 42)', why: 'Arctic textPrimary（深灰蓝）—— 错读会变红 rgb(255,23,32)' },
  { in: '#FFD3DCE6', want: 'rgb(211, 220, 230)', why: 'Arctic border —— 错读会偏粉 rgb(255,211,220)' },
  { in: '#FFE9EFF6', want: 'rgb(233, 239, 246)', why: 'Arctic navigation —— 错读会偏粉 rgb(255,233,239)' },
  { in: '#FF14161A', want: 'rgb(20, 22, 26)', why: 'Graphite window —— 错读会变近透明红 rgba(255,20,22,0.10)' },
  { in: '#FF0B0F14', want: 'rgb(11, 15, 20)', why: 'Obsidian window' },
  { in: '#FF070B18', want: 'rgb(7, 11, 24)', why: 'Midnight window' },

  // ---- 非 ARGB 输入必须原样返回（不猜测、不改写）----
  { in: '#0b0f14', want: '#0b0f14', why: '6 位十六进制不归这个函数管' },
  { in: 'rgba(1, 2, 3, 0.5)', want: 'rgba(1, 2, 3, 0.5)', why: '已经是 CSS 颜色' },
  { in: 'transparent', want: 'transparent', why: '关键字' },
  { in: '#GGGGGGGG', want: '#GGGGGGGG', why: '非法十六进制不得被静默改写' }
]

let pass = 0
const failures = []
for (const c of cases) {
  const got = toCssColor(c.in)
  const ok = got === c.want
  if (ok) pass++
  else failures.push(`${c.in} → 期望 ${c.want}，实际 ${got}  （${c.why}）`)
}

console.log('')
console.log('  主题颜色编码回归（ARGB → CSS）：')
for (const c of cases) {
  const got = toCssColor(c.in)
  console.log(`    ${got === c.want ? 'PASS' : 'FAIL'}  ${c.in} → ${got}   ${c.why}`)
}

// alpha 通道读取
const alphaCases = [
  { in: '#FFFFFFFF', want: 255 },
  { in: '#80FF0000', want: 128 },
  { in: '#00FFFFFF', want: 0 },
  { in: '#0b0f14', want: null }
]
let alphaPass = 0
for (const c of alphaCases) {
  const got = argbAlpha(c.in)
  const ok = got === c.want
  if (ok) alphaPass++
  else failures.push(`argbAlpha(${c.in}) → 期望 ${c.want}，实际 ${got}`)
}

console.log('')
console.log(`  编码用例：${pass}/${cases.length} PASS；alpha 用例：${alphaPass}/${alphaCases.length} PASS`)

// ---- 关键反例：证明本测试能抓住回归 ----
// 把"原样写入"（缺陷行为）当成实现来跑同样的断言，必须大面积失败。
const buggy = (v) => v
const buggyPasses = cases.filter((c) => buggy(c.in) === c.want).length
console.log(`  反例（原样写入 / 缺陷行为）通过数：${buggyPasses}/${cases.length} —— 必须远小于全部，否则测试是恒真指示器`)
if (buggyPasses === cases.length) failures.push('反例控制失效：缺陷实现也全通过，测试无鉴别力')

if (failures.length > 0) {
  console.log('')
  for (const f of failures) console.log('  !! ' + f)
  console.log('')
  console.log(`  结果：FAIL（${failures.length} 项）`)
  process.exit(1)
}
console.log('')
console.log('  结果：PASS')
