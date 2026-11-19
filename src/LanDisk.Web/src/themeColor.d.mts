/** `themeColor.mjs` 的类型声明。实现是纯 ESM，供应用与 node 回归测试共同引用。 */

/** 调色板 `#AARRGGBB` → CSS `rgb()` / `rgba()`；非 8 位十六进制输入原样返回。 */
export function toCssColor(value: string): string

/** 调色板里的 alpha 通道（0–255）；非 ARGB 输入返回 null。 */
export function argbAlpha(value: string): number | null
