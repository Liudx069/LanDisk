/**
 * 项目调色板 (#AARRGGBB) → CSS 颜色的唯一转换点。
 *
 * <h3>为什么必须存在</h3>
 *
 * 调色板用的编码是 **#AARRGGBB**（与桌面端 WPF 一致），而 **CSS 的 8 位十六进制是 #RRGGBBAA**。
 * 把调色板字符串原样写进 CSS 变量，浏览器会把每个字节整体错位一位：
 *
 * ```
 *   #AARRGGBB            CSS 实际读成 #RRGGBBAA
 *   A → R,  R → G,  G → B,  B → A
 * ```
 *
 * 真机上的后果（Owner 截图）：`#FFF4F7FB`（Arctic 的浅蓝 window）被读成 `rgba(255,244,247,0.984)`
 * —— 背景泛粉；accent `#FF0A74C4` 读成 `rgb(255,10,116)` —— 按钮品红；textPrimary `#FF17202A`
 * 读成 `rgb(255,23,32)` —— 文字发红；border、navigation 同样偏粉；alpha 全部错乱导致层次与
 * 对比度崩坏。**整页同时被染色，就是因为同一个编码错误作用在每一个 role 上。**
 *
 * 该模块被应用与测试**共同引用**，因此测试验证的就是线上运行的那份实现。
 */

/**
 * 把 `#AARRGGBB` 转成 CSS 能正确解读的 `rgb()` / `rgba()`。
 *
 * 非 8 位十六进制的输入（例如已经是 `rgb(...)`、或 6 位 `#RRGGBB`、或其它格式）原样返回 ——
 * 不猜测、不改写调用方没要求改的东西。
 *
 * @param {string} value 调色板里的颜色值
 * @returns {string} CSS 颜色
 */
export function toCssColor(value) {
  if (typeof value !== 'string') return value
  const hex = value.trim().replace('#', '')
  if (hex.length !== 8 || !/^[0-9a-fA-F]{8}$/.test(hex)) return value

  const a = parseInt(hex.slice(0, 2), 16)
  const r = parseInt(hex.slice(2, 4), 16)
  const g = parseInt(hex.slice(4, 6), 16)
  const b = parseInt(hex.slice(6, 8), 16)
  if (![a, r, g, b].every((n) => Number.isFinite(n))) return value

  // 完全不透明时用 rgb()：少一个通道，也让"这是不透明色"在代码里一眼可见。
  if (a >= 255) return `rgb(${r}, ${g}, ${b})`

  // 去尾零：0.000 → 0、0.500 → 0.5。值会直接进 DOM（`--window: rgba(...)`），
  // 保留 "0.000" 这种写法只是噪声。
  const alphaText = (a / 255).toFixed(3).replace(/0+$/, '').replace(/\.$/, '') || '0'
  return `rgba(${r}, ${g}, ${b}, ${alphaText})`
}

/**
 * 调色板里的 alpha 通道（0–255）。
 * @param {string} value
 * @returns {number|null} 非 ARGB 输入返回 null
 */
export function argbAlpha(value) {
  if (typeof value !== 'string') return null
  const hex = value.trim().replace('#', '')
  if (hex.length !== 8 || !/^[0-9a-fA-F]{8}$/.test(hex)) return null
  return parseInt(hex.slice(0, 2), 16)
}
