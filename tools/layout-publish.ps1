# 发布目录整理：把托管依赖收进 runtime 子目录，根目录只留启动器、宿主与原生库。
# 原生库必须留根：WPF 的 wpfgfx / PresentationNative 会按 exe 同目录加载 D3DCompiler_47_cor3 等依赖；
# System.Private.CoreLib 也必须留根：CoreCLR 启动时按应用根目录硬加载它，不走 deps 清单。
# 用法：pwsh -File tools/layout-publish.ps1 -Root publish
param(
  [Parameter(Mandatory = $true)][string]$Root,
  [string]$AppName = 'ChatTranslate'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Root).Path
$runtimeDir = Join-Path $root 'runtime'
$depsPath = Join-Path $root ($AppName + '.deps.json')
if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) { throw "找不到依赖清单: $depsPath" }
$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json

$keepNames = @(
  ($AppName + '.exe'),
  ($AppName + '.dll'),
  ($AppName + '.deps.json'),
  ($AppName + '.runtimeconfig.json'),
  'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'clrjit.dll', 'clrgc.dll',
  'clretwrc.dll', 'mscorrc.dll', 'System.Private.CoreLib.dll',
  'mscordaccore.dll', 'mscordbi.dll', 'createdump.exe',
  'Microsoft.DiaSymReader.Native.amd64.dll'
)

New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null

$target = $deps.targets.PSObject.Properties | Where-Object { $_.Name -like '*/win-x64' } | Select-Object -First 1
if (-not $target) { throw '依赖清单中没有 win-x64 目标' }

$moved = 0
$renamed = 0
foreach ($lib in $target.Value.PSObject.Properties) {
  foreach ($kind in @('runtime', 'native', 'resources', 'runtimeTargets')) {
    $node = $lib.Value.$kind
    if ($null -eq $node) { continue }
    foreach ($asset in @($node.PSObject.Properties)) {
      $assetPath = $asset.Name
      if ($assetPath -like 'runtime/*') { continue }
      $name = [System.IO.Path]::GetFileName($assetPath)
      if ($keepNames -contains $name) { continue }

      $isNative = ($kind -eq 'native') -or ($kind -eq 'runtimeTargets' -and $asset.Value.assetType -eq 'native')
      if ($isNative) {
        if ($assetPath -ne $name -and (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf)) {
          $value = $asset.Value
          $node.PSObject.Properties.Remove($assetPath)
          $node | Add-Member -NotePropertyName $name -NotePropertyValue $value
          $renamed++
        }
        continue
      }

      $source = Join-Path $root $assetPath
      $rel = $assetPath
      if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        $source = Join-Path $root $name
        $rel = $name
      }

      $dest = Join-Path $runtimeDir $rel
      if (Test-Path -LiteralPath $source -PathType Leaf) {
        if (Test-Path -LiteralPath $dest) { throw "目标冲突: $dest" }
        $destDir = Split-Path $dest -Parent
        if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
        Move-Item -LiteralPath $source -Destination $dest
        $moved++
      }
      elseif (-not (Test-Path -LiteralPath $dest -PathType Leaf)) {
        throw "找不到资产: $assetPath ($kind)"
      }

      $newKey = 'runtime/' + ($rel -replace '\\', '/')
      if ($newKey -ne $assetPath) {
        $value = $asset.Value
        $node.PSObject.Properties.Remove($assetPath)
        $node | Add-Member -NotePropertyName $newKey -NotePropertyValue $value
        $renamed++
      }
    }
  }
}

$culture = Join-Path $root 'zh-Hans'
if (Test-Path -LiteralPath $culture) {
  $left = @(Get-ChildItem -LiteralPath $culture -Recurse -File -ErrorAction SilentlyContinue)
  if ($left.Count -eq 0) {
    try { [System.IO.Directory]::Delete($culture, $true) } catch { }
  }
}

$json = $deps | ConvertTo-Json -Depth 100
[System.IO.File]::WriteAllText($depsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
"moved=$moved renamed=$renamed rootFiles=$((Get-ChildItem -LiteralPath $root -File).Count)"



