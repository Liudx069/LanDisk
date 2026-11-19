# 网络标识脱敏：真实地址与网卡型号统一替换为固定的掩码标识。
#
# 设计约束（来自交付要求）：
#   * 掩码**一致**：同一类值总是同一个标识，读者能看出"这里被脱敏了"，不会误以为是真实值；
#   * **不使用虚构地址**：绝不把 192.168.x.x 换成另一个看起来真实的 IP —— 那会让人以为它是实测监听值；
#   * **保留核验信息**：version / commit / 端口 / 证书指纹 / 测试结果 / 产物 SHA256 / 测试身份一律不动；
#   * 端口号、.local 稳定名称、install id 属于核验所需，保留（install id 与主机名只在机器自身身份证据里）。
#
# 只做必要替换，不另建脱敏框架。

function Mask-NetworkText {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrEmpty($Text)) { return $Text }

    $masked = $Text

    # 私网地址 → 固定掩码（不换成别的地址）
    $masked = [regex]::Replace($masked, '\b(?:10\.\d{1,3}|192\.168\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3})\.\d{1,3}\b', '<已脱敏-私网地址>')

    # 网卡型号 / 厂商 → 固定掩码
    # 排除右括号与句读，否则会把紧跟其后的「）」一并吞掉（实测出现过）
    $masked = [regex]::Replace($masked, 'Realtek[^"“”\r\n,，;；|）)】\]。]*', '<已脱敏-网卡型号>')
    $masked = [regex]::Replace($masked, 'Intel\(R\)[^"“”\r\n,，;；|）)】\]。]*', '<已脱敏-网卡型号>')
    $masked = [regex]::Replace($masked, '\bRTL\d{4}[A-Za-z]*', '<已脱敏-网卡型号>')

    # 无线网络名：只处理明确作为 SSID 出现的形态，避免误伤页面名 "WiFi"
    $masked = [regex]::Replace($masked, '"ssid"\s*:\s*"[^"]*"', '"ssid": "<已脱敏-无线网络名>"')

    return $masked
}

function Mask-NetworkFile {
    param([Parameter(Mandatory)][string]$SourcePath, [Parameter(Mandatory)][string]$DestinationPath)
    $text = [System.IO.File]::ReadAllText($SourcePath, [System.Text.Encoding]::UTF8)
    $masked = Mask-NetworkText -Text $text
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DestinationPath) | Out-Null
    # 保持 UTF-8 无 BOM，与源文件一致
    [System.IO.File]::WriteAllText($DestinationPath, $masked, (New-Object System.Text.UTF8Encoding($false)))
    return ($masked -ne $text)
}
