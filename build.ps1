$ErrorActionPreference = "Stop"

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "未找到 .NET Framework C# 编译器：$csc"
}

$output = "ZhiBanTai.exe"

& $csc `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /out:$output `
    /win32icon:"ZhiBanTai.ico" `
    /r:System.dll `
    /r:System.Core.dll `
    /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll `
    "LanMonitor.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

Write-Host "Build complete: $output"
