param([string]$Destination = '.local/tools/vgmstream', [string]$CMakePath)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$component = (Get-Content -LiteralPath (Join-Path $workspace 'tools/components.lock.json') -Raw | ConvertFrom-Json).components | Where-Object { $_.name -eq 'vgmstream' }
$downloads = Join-Path $workspace '.local/downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
$archive = Join-Path $downloads 'vgmstream-source.zip'
if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $component.sourceArchive -OutFile $archive -UseBasicParsing }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $component.sourceSha256) { throw 'Decoder source checksum mismatch.' }
$expanded = Join-Path $downloads 'vgmstream-source'
Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
$source = Get-ChildItem -LiteralPath $expanded -Directory | Select-Object -First 1
if (!$CMakePath) {
    $command = Get-Command cmake -ErrorAction SilentlyContinue
    if ($command) { $CMakePath = $command.Source }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (!$installation) { throw 'Install Visual Studio C++ build tools, or supply CMakePath.' }
        $CMakePath = Join-Path $installation 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    }
}
$build = Join-Path $workspace '.local/decoder-build'
$flags = @('-DBUILD_CLI=ON','-DBUILD_FB2K=OFF','-DBUILD_WINAMP=OFF','-DBUILD_XMPLAY=OFF','-DUSE_VORBIS=ON','-DUSE_MPEG=OFF','-DUSE_FFMPEG=OFF','-DUSE_G7221=OFF','-DUSE_G719=OFF','-DUSE_ATRAC9=OFF','-DUSE_CELT=OFF','-DUSE_SPEEX=OFF','-DCMAKE_C_FLAGS_RELEASE=/MT /O2 /DNDEBUG','-DCMAKE_CXX_FLAGS_RELEASE=/MT /O2 /DNDEBUG')
& $CMakePath -S $source.FullName -B $build -A x64 @flags
if ($LASTEXITCODE -ne 0) { throw 'Decoder configuration failed.' }
& $CMakePath --build $build --config Release --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'Decoder build failed.' }
$target = if ([IO.Path]::IsPathRooted($Destination)) { [IO.Path]::GetFullPath($Destination) } else { [IO.Path]::GetFullPath((Join-Path $workspace $Destination)) }
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'cli/Release/vgmstream-cli.exe') -Destination (Join-Path $target 'vgmstream-cli.exe')
Write-Output ('Decoder ready: ' + $target)
