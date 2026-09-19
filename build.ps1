param([switch]$TestsOnly,[string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$panelRoot = $PSScriptRoot
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$panelBuild = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $panelRoot 'build' }
New-Item -ItemType Directory -Path $panelBuild -Force | Out-Null
$core = Join-Path $panelRoot 'src\PanelCore.cs'
$catalogReader = Join-Path $panelRoot 'src\CatalogReader.cs'
$testSource = Join-Path $panelRoot 'tests\CoreTests.cs'
$testExe = Join-Path $panelBuild 'RniPanel.Tests.exe'
& $compiler /nologo /utf8output /target:exe /platform:x64 "/out:$testExe" /r:System.Core.dll /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll $core $catalogReader $testSource
if ($LASTEXITCODE -ne 0) { throw 'Core test build failed.' }
if (!$TestsOnly) {
    $panelExe = Join-Path $panelBuild 'RniPanel.exe'
    $bridge = Join-Path $panelRoot 'src\NativeBridge.cs'
    $batch = Join-Path $panelRoot 'src\NativeBatch.cs'
    $window = Join-Path $panelRoot 'src\App.cs'
    & $compiler /nologo /utf8output /target:winexe /platform:x64 "/out:$panelExe" /r:System.Core.dll /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll $core $catalogReader $bridge $batch $window
    if ($LASTEXITCODE -ne 0) { throw 'Panel build failed.' }
}
