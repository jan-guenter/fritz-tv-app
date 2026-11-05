[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($PublishDirectory)
if (-not (Test-Path (Join-Path $target 'FritzTV.Desktop.exe'))) { throw 'Publish the Windows application before bundling dependencies.' }
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('fritz-tv-mkv-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $archive = Join-Path $temporary 'mkvtoolnix-103.0.zip'
    Invoke-WebRequest 'https://mkvtoolnix.download/windows/releases/103.0/mkvtoolnix-64-bit-103.0.zip' -OutFile $archive
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne 'c860a10c707cb1ee587d1464f6171842f2ada3bb6f031dfff55fec75542f0417') { throw 'MKVToolNix checksum mismatch.' }
    Expand-Archive $archive -DestinationPath (Join-Path $temporary 'expanded')
    $source = Join-Path $temporary 'expanded/mkvtoolnix'
    if (-not (Test-Path (Join-Path $source 'mkvmerge.exe'))) { throw 'The verified archive did not contain mkvmerge.exe.' }
    $tools = Join-Path $target 'tools'
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    $bundle = Join-Path $tools 'mkvtoolnix'
    if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
    Copy-Item $source -Destination $bundle -Recurse
    & (Join-Path $bundle 'mkvmerge.exe') --version
    if ($LASTEXITCODE -ne 0) { throw 'The bundled mkvmerge executable did not run.' }
    Copy-Item (Join-Path $PSScriptRoot '../THIRD-PARTY-NOTICES.md') -Destination $target
    Copy-Item (Join-Path $PSScriptRoot '../LICENSE') -Destination $target
    Write-Output 'Bundled MKVToolNix 103.0 with its original DLLs and licence files.'
}
finally {
    Remove-Item $temporary -Recurse -Force
}
