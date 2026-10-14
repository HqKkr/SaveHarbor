param([string]$OutputFile = (Join-Path $PSScriptRoot 'SaveHarbor.exe'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Drawing.dll','System.Security.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Xml.dll','System.Xml.Linq.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path "$framework\WPF" $_) }
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Select-Object -ExpandProperty FullName)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputFile) -Force | Out-Null
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /out:$OutputFile $references $sources
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
if ([System.IO.Path]::GetFullPath($OutputFile + '.config') -ne [System.IO.Path]::GetFullPath("$PSScriptRoot\SaveHarbor.exe.config")) { Copy-Item -LiteralPath "$PSScriptRoot\SaveHarbor.exe.config" -Destination ($OutputFile + '.config') -Force }
