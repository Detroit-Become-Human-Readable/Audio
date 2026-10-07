param([string]$Destination = '.local/tools')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$destinationPath = if ([IO.Path]::IsPathRooted($Destination)) { [IO.Path]::GetFullPath($Destination) } else { [IO.Path]::GetFullPath((Join-Path $workspace $Destination)) }
$downloads = Join-Path $workspace '.local/downloads'
New-Item -ItemType Directory -Path $destinationPath,$downloads -Force | Out-Null
$lock = Get-Content -LiteralPath (Join-Path $workspace 'tools/components.lock.json') -Raw | ConvertFrom-Json
foreach ($component in $lock.components) {
    $extension = if ($component.archive -eq 'zip') { '.zip' } else { '.exe' }
    $download = Join-Path $downloads ($component.name + '-pinned' + $extension)
    if (!(Test-Path -LiteralPath $download)) { Invoke-WebRequest -Uri $component.url -OutFile $download -UseBasicParsing }
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $component.sha256) { throw ($component.name + ' checksum mismatch. No files were installed.') }
    $target = Join-Path $destinationPath $component.name
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    if ($component.archive -eq 'zip') {
        $expanded = Join-Path $downloads ($component.name + '-pinned-expanded')
        Expand-Archive -LiteralPath $download -DestinationPath $expanded -Force
        foreach ($name in $component.files) {
            $source = Get-ChildItem -LiteralPath $expanded -Recurse -File | Where-Object { $_.Name -eq $name } | Select-Object -First 1
            if (!$source) { throw ('Missing upstream file ' + $name) }
            Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $target $name)
        }
    } else { Copy-Item -LiteralPath $download -Destination (Join-Path $target $component.files[0]) }
}
$notices = Join-Path $destinationPath 'notices'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
$licenses = @(
    @('vgmstream-ISC.txt','https://raw.githubusercontent.com/vgmstream/vgmstream/7dc938fa/COPYING'),
    @('ww2ogg-BSD.txt','https://raw.githubusercontent.com/hcs64/ww2ogg/master/COPYING'),
    @('ReVorb-ISC.txt','https://raw.githubusercontent.com/ItsBranK/ReVorb/main/revorb.cpp'),
    @('libvorbis-BSD.txt','https://raw.githubusercontent.com/xiph/vorbis/master/COPYING'),
    @('libogg-BSD.txt','https://raw.githubusercontent.com/xiph/ogg/master/COPYING'),
    @('FFmpeg-LGPL-3.txt','https://raw.githubusercontent.com/FFmpeg/FFmpeg/n8.1.3/COPYING.LGPLv3'),
    @('GPL-3.txt','https://raw.githubusercontent.com/FFmpeg/FFmpeg/n8.1.3/COPYING.GPLv3'))
foreach ($pair in $licenses) {
    $response = Invoke-WebRequest -Uri $pair[1] -UseBasicParsing
    $body = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { $response.Content }
    if ($pair[0] -like 'ReVorb*') { $body = ($body -split '\*/',2)[0] + '*/' }
    [IO.File]::WriteAllText((Join-Path $notices $pair[0]),$body,[Text.Encoding]::UTF8)
}
Copy-Item -LiteralPath (Join-Path $workspace 'tools/components.lock.json') -Destination (Join-Path $destinationPath 'components.lock.json')
& (Join-Path $PSScriptRoot 'Build-Decoder.ps1') -Destination (Join-Path $destinationPath 'vgmstream')
& (Join-Path $PSScriptRoot 'Create-ToolResources.ps1') -ToolsDirectory $destinationPath
Write-Output ('Tools ready: ' + $destinationPath)
