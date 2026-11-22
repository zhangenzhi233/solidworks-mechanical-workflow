param([string]$SolidWorksDirectory)
$ErrorActionPreference = 'Stop'
if (-not $SolidWorksDirectory) {
    $running = Get-Process -Name SLDWORKS -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($running -and $running.Path) {
        $SolidWorksDirectory = Split-Path -Parent $running.Path
    } else {
        $setup = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\SolidWorks\SOLIDWORKS 2024\Setup' -ErrorAction SilentlyContinue
        $SolidWorksDirectory = $setup.'SolidWorks Folder'
    }
}
if (-not $SolidWorksDirectory) { throw '请先打开 SOLIDWORKS 2024，或通过 -SolidWorksDirectory 指定其程序目录。' }
$libraries = Join-Path $SolidWorksDirectory 'api\redist'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$target = Join-Path (Split-Path -Parent $PSScriptRoot) '台钳手推实验.exe'
$sources = 'Hand.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /platform:x64 /codepage:65001 /target:winexe "/out:$target" "/reference:$libraries\SolidWorks.Interop.sldworks.dll" "/reference:$libraries\SolidWorks.Interop.swconst.dll" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll $sources
if ($LASTEXITCODE -ne 0) { throw "编译退出码 $LASTEXITCODE" }
Get-Item -LiteralPath $target
