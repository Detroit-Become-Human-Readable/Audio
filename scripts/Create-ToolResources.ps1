param([string]$ToolsDirectory = '.local/tools')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$toolRoot = if ([IO.Path]::IsPathRooted($ToolsDirectory)) { [IO.Path]::GetFullPath($ToolsDirectory) } else { [IO.Path]::GetFullPath((Join-Path $workspace $ToolsDirectory)) }
$assets = Join-Path $workspace 'src/DetroitAudio.Audio/Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$lock = Get-Content -LiteralPath (Join-Path $workspace 'tools/components.lock.json') -Raw | ConvertFrom-Json
$temporary = Join-Path $assets ('tool-seeds-' + [Guid]::NewGuid().ToString('N') + '.zip')
Add-Type -AssemblyName System.IO.Compression
$file = [IO.File]::Open($temporary,[IO.FileMode]::CreateNew)
try {
    $archive = New-Object IO.Compression.ZipArchive($file,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        foreach ($component in ($lock.components | Where-Object { $_.name -ne 'ffmpeg' })) {
            foreach ($name in $component.files) {
                $source = Join-Path $toolRoot ($component.name + '/' + $name)
                if (!(Test-Path -LiteralPath $source)) { throw ('Missing tool file: ' + $source) }
                $entry = $archive.CreateEntry(($component.name + '/' + $name),[IO.Compression.CompressionLevel]::Optimal)
                $stream = $entry.Open()
                $input = [IO.File]::OpenRead($source)
                try { $input.CopyTo($stream) } finally { $input.Dispose(); $stream.Dispose() }
            }
        }
    } finally { $archive.Dispose() }
} finally { $file.Dispose() }
$seed = Join-Path $assets 'tool-seeds.zip'
Move-Item -LiteralPath $temporary -Destination $seed -Force
$seedHash = (Get-FileHash -LiteralPath $seed -Algorithm SHA256).Hash
$labels = @{vgmstream='Audio decoder';ffmpeg='Audio conversion';ww2ogg='Vorbis export';revorb='OGG repair'}
$specs = @()
foreach ($component in $lock.components) {
    $files = @($component.files | ForEach-Object { [pscustomobject]@{Path=$_;Sha256=(Get-FileHash -LiteralPath (Join-Path $toolRoot ($component.name + '/' + $_)) -Algorithm SHA256).Hash} })
    $embedded = $component.name -ne 'ffmpeg'
    $specs += [pscustomobject]@{Name=$component.name;DisplayName=$labels[$component.name];Version=$component.version;Url=$(if($embedded){$null}else{$component.url});Archive='zip';Sha256=$(if($embedded){$seedHash}else{$component.sha256});Files=$files;SeedArchive=$(if($embedded){'DetroitAudio.Audio.Assets.tool-seeds.zip'}else{$null});SeedPath=$(if($embedded){$component.name}else{$null})}
}
$specs | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $assets 'install-tools.json') -Encoding UTF8
Write-Output 'Runtime tool resources ready.'
