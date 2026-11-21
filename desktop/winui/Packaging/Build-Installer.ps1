param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputFile)
if ($output.StartsWith($publish.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an installer output outside the application directory.'
}
if (!(Test-Path -LiteralPath (Join-Path $publish 'MeshMonitor.Native.exe'))) { throw 'Publish the Windows x64 app first.' }
$temp = Join-Path ([IO.Path]::GetDirectoryName($output)) ('setup-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path $temp 'MeshMonitor.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $archive, [IO.Compression.CompressionLevel]::Fastest, $false)
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $binary = Join-Path $temp 'Setup.exe'
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$binary" "/resource:$archive,MeshMonitor.zip" "/win32manifest:$PSScriptRoot/Setup.manifest" "/win32icon:$PSScriptRoot/../MeshMonitor.ico" /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:Microsoft.CSharp.dll (Join-Path $PSScriptRoot 'Setup.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    [IO.File]::Copy($binary, $output, $true)
}
finally {
    [IO.Directory]::Delete($temp, $true)
}
