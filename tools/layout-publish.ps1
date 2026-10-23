# 把自包含发布产物收进 ChatTranslate 子目录，交付包根目录只保留这一个文件夹。
# 不能把框架 DLL 抽到 lib/runtime：自包含模式下 host 只从 exe 所在目录加载
# PresentationFramework 等框架程序集，移走会 FileNotFoundException 启动失败。
# 用法：pwsh -File tools/layout-publish.ps1 -Root publish
param(
  [string]$Root = 'publish',
  [string]$AppName = 'ChatTranslate'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Root).Path
$target = Join-Path $root $AppName

if (Test-Path -LiteralPath (Join-Path $target ($AppName + '.exe')) -PathType Leaf) {
  "已整理: $target"
  return
}
if (-not (Test-Path -LiteralPath (Join-Path $root ($AppName + '.exe')) -PathType Leaf)) {
  throw "找不到 $AppName.exe：$root"
}
if (Test-Path -LiteralPath $target) { throw "目标已存在: $target" }

New-Item -ItemType Directory -Path $target | Out-Null
Get-ChildItem -LiteralPath $root -Force |
  Where-Object { $_.FullName -ne $target } |
  Move-Item -Destination $target
"已移入: $target"
