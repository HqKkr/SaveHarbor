$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Xml.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path "$framework\WPF" $_) }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /out:"$PSScriptRoot\SaveHarbor.exe" $references "$PSScriptRoot\SaveHarbor.cs"
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
