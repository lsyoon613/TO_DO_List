$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) {
    throw 'Windows with .NET Framework 4.x is required to build this app.'
}
$refs = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
& "$framework\csc.exe" /nologo /target:winexe /out:"$PSScriptRoot\TrayTodo.exe" /resource:"$PSScriptRoot\Main.xaml",Main.xaml $refs "$PSScriptRoot\TrayTodo.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output "Built: $PSScriptRoot\TrayTodo.exe"
