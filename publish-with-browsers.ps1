# 发布脚本：打包浏览器到插件存储区

$ErrorActionPreference = "Stop"

Write-Host "=== 开始发布 Plugin.NetworkTools（含浏览器）===" -ForegroundColor Green

# 1. 编译插件
Write-Host "`n[1/3] 编译插件..." -ForegroundColor Cyan
cd Plugins/Plugin.NetworkTools
dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败！" -ForegroundColor Red
    exit 1
}

# 2. 复制浏览器到 Storage
Write-Host "`n[2/3] 复制浏览器到 Storage..." -ForegroundColor Cyan
$storageBase = "..\..\Storage\Plugins\Plugin.NetworkTools\Browsers"
New-Item -ItemType Directory -Force -Path $storageBase | Out-Null

if (Test-Path "D:\Playwright-browsers\chromium-1097") {
    Copy-Item -Recurse -Force "D:\Playwright-browsers\chromium-1097" "$storageBase\chromium-1097"
    Write-Host "  ✓ chromium-1097 已复制"
} else {
    Write-Host "  ✗ 未找到 D:\Playwright-browsers\chromium-1097" -ForegroundColor Yellow
}

if (Test-Path "D:\Playwright-browsers\ffmpeg-1009") {
    Copy-Item -Recurse -Force "D:\Playwright-browsers\ffmpeg-1009" "$storageBase\ffmpeg-1009"
    Write-Host "  ✓ ffmpeg-1009 已复制"
} else {
    Write-Host "  ✗ 未找到 D:\Playwright-browsers\ffmpeg-1009" -ForegroundColor Yellow
}

# 3. 统计大小
Write-Host "`n[3/3] 统计发布包大小..." -ForegroundColor Cyan
$browserSize = (Get-ChildItem -Recurse $storageBase | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "  浏览器大小: $([math]::Round($browserSize, 2)) MB"

Write-Host "`n=== 发布完成！===" -ForegroundColor Green
Write-Host "浏览器已打包到: $storageBase" -ForegroundColor Green
