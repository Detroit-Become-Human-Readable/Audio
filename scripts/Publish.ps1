param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$ToolsDirectory = '.local/tools',
    [string]$OutputDirectory = 'artifacts/DetroitAudio',
    [string]$PackagesPath,
    [string]$PackageSource,
    [string]$RuntimeFrameworkVersion
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $workspace $OutputDirectory)) }
$toolRoot = if ([IO.Path]::IsPathRooted($ToolsDirectory)) { [IO.Path]::GetFullPath($ToolsDirectory) } else { [IO.Path]::GetFullPath((Join-Path $workspace $ToolsDirectory)) }
if ($output.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -eq [IO.Path]::GetPathRoot($output).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) { throw 'The publish output must be a directory below a filesystem root.' }
$output = $output.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$manifestName = '.detroit-audio-publish.json'
$preserveBackup = $false
$parent = Split-Path -Parent $output
function Assert-NoReparsePoints([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (![string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Publisher output paths cannot traverse reparse points: $current" }
        }
        $next = [IO.Path]::GetDirectoryName($current.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
        if ([string]::IsNullOrWhiteSpace($next) -or $next -eq $current) { break }
        $current = $next
    }
}
Assert-NoReparsePoints $output
if (!(Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
Assert-NoReparsePoints $parent
$stage = Join-Path $parent ('.' + (Split-Path -Leaf $output) + '.staging-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $parent ('.' + (Split-Path -Leaf $output) + '.backup-' + [Guid]::NewGuid().ToString('N'))

function Get-SafeOwnedPath([string]$Root, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath) -or $RelativePath.Contains(':')) { throw "Invalid path in publisher ownership manifest: $RelativePath" }
    $parts = $RelativePath -split '[\\/]'
    if ($parts | Where-Object { $_ -in @('', '.', '..') }) { throw "Invalid path in publisher ownership manifest: $RelativePath" }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath((Join-Path $Root ($parts -join [IO.Path]::DirectorySeparatorChar)))
    if (!$fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) { throw "Publisher-owned path escapes its output directory: $RelativePath" }
    Assert-NoReparsePoints $fullPath
    return $fullPath
}

function Get-LegacyOwnedFiles([string]$Root, [string]$ManifestPath) {
    $knownExecutables = @('DetroitAudio.exe', 'Detroit Audio Extractor.exe')
    $existingExecutables = @($knownExecutables | Where-Object { Test-Path -LiteralPath (Get-SafeOwnedPath $Root $_) -PathType Leaf })
    if ($existingExecutables.Count -ne 1) { return @() }
    $required = @($existingExecutables[0], 'LICENSE','THIRD-PARTY-NOTICES.md','README.md','tools/components.lock.json','tools/manifest.json')
    foreach ($relative in $required) {
        $path = Get-SafeOwnedPath $Root $relative
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return @() }
    }
    try {
        $currentLock = Get-Content -LiteralPath (Join-Path $workspace 'tools/components.lock.json') -Raw | ConvertFrom-Json
        $legacyLock = Get-Content -LiteralPath (Join-Path $Root 'tools/components.lock.json') -Raw | ConvertFrom-Json
        $legacyToolManifest = Get-Content -LiteralPath (Join-Path $Root 'tools/manifest.json') -Raw | ConvertFrom-Json
        $legacyToolManifest = @($legacyToolManifest)
        if ($currentLock.version -ne $legacyLock.version -or $currentLock.components.Count -ne $legacyLock.components.Count) { return @() }
        $expected = @{}
        foreach ($component in $currentLock.components) {
            $legacyComponent = $legacyLock.components | Where-Object { $_.name -eq $component.name } | Select-Object -First 1
            if (!$legacyComponent -or $legacyComponent.version -ne $component.version -or (@($legacyComponent.files) -join "`n") -ne (@($component.files) -join "`n")) { return @() }
            foreach ($name in $component.files) { $expected['tools/' + $component.name + '/' + $name] = $component.name }
        }
        if ($legacyToolManifest.Count -ne $expected.Count) { return @() }
        $seen = @{}
        foreach ($entry in $legacyToolManifest) {
            $relative = [string]$entry.file
            if (!$expected.ContainsKey($relative) -or $seen.ContainsKey($relative) -or $entry.component -ne $expected[$relative] -or [string]$entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { return @() }
            $toolPath = Get-SafeOwnedPath $Root $relative
            if (!(Test-Path -LiteralPath $toolPath -PathType Leaf) -or (Get-FileHash -LiteralPath $toolPath -Algorithm SHA256).Hash -ne $entry.sha256) { return @() }
            $seen[$relative] = $true
        }
        if ($seen.Count -ne $expected.Count) { return @() }
        $owned = @($required + @($expected.Keys))
        $knownNotices = @('vgmstream-ISC.txt','vgmstream-dependencies.md','ww2ogg-BSD.txt','ReVorb-ISC.txt','libvorbis-BSD.txt','libogg-BSD.txt','FFmpeg-LGPL-3.txt','GPL-3.txt')
        foreach ($notice in $knownNotices) {
            $relative = 'tools/notices/' + $notice
            if (Test-Path -LiteralPath (Get-SafeOwnedPath $Root $relative) -PathType Leaf) { $owned += $relative }
        }
        return @($owned | Sort-Object -Unique)
    } catch { return @() }
}

function Read-OwnedFiles([string]$Root, [string]$ManifestPath) {
    Assert-NoReparsePoints $ManifestPath
    if (!(Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { return @(Get-LegacyOwnedFiles $Root $ManifestPath) }
    $data = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($data.version -ne 1 -or $data.files -isnot [System.Array]) { throw 'The existing publisher ownership manifest is invalid.' }
    $files = @($data.files | ForEach-Object { [string]$_ })
    foreach ($relative in $files) { [void](Get-SafeOwnedPath $Root $relative) }
    if (@($files | Sort-Object -Unique).Count -ne $files.Count) { throw 'The existing publisher ownership manifest contains duplicate paths.' }
    return $files
}

function Get-RelativeFiles([string]$Root) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse | ForEach-Object { $_.FullName.Substring($base.Length).Replace([IO.Path]::DirectorySeparatorChar, '/') } | Sort-Object -Unique)
}

try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $arguments = @('publish',(Join-Path $workspace 'src/DetroitAudio.Desktop/DetroitAudio.Desktop.csproj'),'-c',$Configuration,'-r','win-x64','--self-contained','true','-o',$stage,'-p:AutoPublish=false','-p:NuGetAudit=false','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:PublishTrimmed=false','-p:DebugType=embedded')
    if ($PackagesPath) { $arguments += ('-p:RestorePackagesPath=' + $PackagesPath) }
    if ($PackageSource) { $arguments += @('--source', $PackageSource) }
    if ($RuntimeFrameworkVersion) { $arguments += ('-p:RuntimeFrameworkVersion=' + $RuntimeFrameworkVersion) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    $executables = @(Get-ChildItem -LiteralPath $stage -File | Where-Object { $_.Extension -ieq '.exe' })
    if ($executables.Count -ne 1) { throw 'The published application must contain exactly one root executable.' }
    $executableName = $executables[0].Name
    [void](Get-SafeOwnedPath $stage $executableName)
    $assemblyBaseName = [IO.Path]::GetFileNameWithoutExtension($executableName)
    foreach ($stale in (Get-ChildItem -LiteralPath $stage -File | Where-Object { $_.Extension -in @('.dll','.pdb') -or $_.Name -in @("${assemblyBaseName}.deps.json", "${assemblyBaseName}.runtimeconfig.json") })) {
        Remove-Item -LiteralPath $stale.FullName -ErrorAction Stop
    }
    foreach ($culture in (Get-ChildItem -LiteralPath $stage -Directory | Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]{2,4})?$' })) {
        $satellites = @(Get-ChildItem -LiteralPath $culture.FullName -File)
        if (@(Get-ChildItem -LiteralPath $culture.FullName -Force).Count -eq 0 -or
            ($satellites.Count -gt 0 -and @($satellites | Where-Object { $_.Name -notlike '*.resources.dll' }).Count -eq 0 -and @(Get-ChildItem -LiteralPath $culture.FullName -Directory).Count -eq 0)) {
            Remove-Item -LiteralPath $culture.FullName -Recurse -Force -ErrorAction Stop
        }
    }
    $lock = Get-Content -LiteralPath (Join-Path $workspace 'tools/components.lock.json') -Raw | ConvertFrom-Json
    $runtimeLockPath = Join-Path $workspace 'src/DetroitAudio.Audio/Assets/install-tools.json'
    if (!(Test-Path -LiteralPath $runtimeLockPath -PathType Leaf)) { throw 'The runtime tool manifest is missing.' }
    $runtimeComponents = Get-Content -LiteralPath $runtimeLockPath -Raw | ConvertFrom-Json
    $runtimeComponents = @($runtimeComponents)
    if ($runtimeComponents.Count -ne $lock.components.Count) { throw 'The runtime tool manifest does not match the component lock.' }
    $toolManifest = @()
    foreach ($component in $lock.components) {
        $runtimeMatches = @($runtimeComponents | Where-Object { $_.Name -ceq $component.name })
        if ($runtimeMatches.Count -ne 1 -or $runtimeMatches[0].Version -cne $component.version) { throw "The runtime tool version for '$($component.name)' does not match the component lock." }
        $runtimeFiles = @($runtimeMatches[0].Files)
        if ($runtimeFiles.Count -ne $component.files.Count) { throw "The runtime tool file list for '$($component.name)' does not match the component lock." }
        $destination = Join-Path $stage ('tools/' + $component.name)
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($name in $component.files) {
            $runtimeFileMatches = @($runtimeFiles | Where-Object { $_.Path -ceq $name })
            if ($runtimeFileMatches.Count -ne 1 -or [string]$runtimeFileMatches[0].Sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw "The runtime tool hash for '$($component.name)/$name' is missing or invalid." }
            $source = Join-Path $toolRoot ($component.name + '/' + $name)
            if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw ('Missing tool file: ' + $name + '. Run Prepare-Tools.ps1 first.') }
            $stagedTool = Join-Path $destination $name
            Copy-Item -LiteralPath $source -Destination $stagedTool
            $actualHash = (Get-FileHash -LiteralPath $stagedTool -Algorithm SHA256).Hash
            if ($actualHash -ine $runtimeFileMatches[0].Sha256) { throw "Tool checksum mismatch for '$($component.name)/$name'. Prepare the pinned tool files again." }
            $toolManifest += [pscustomobject]@{file=('tools/' + $component.name + '/' + $name);sha256=$actualHash;component=$component.name;version=$component.version;license=$component.license;source=$component.source}
        }
    }
    foreach ($name in @('notices')) {
        $source = Join-Path $toolRoot $name
        if (!(Test-Path -LiteralPath $source -PathType Container)) { throw "Missing offline tool notices: $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage 'tools') -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $workspace 'tools/components.lock.json') -Destination (Join-Path $stage 'tools/components.lock.json')
    $toolManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'tools/manifest.json') -Encoding UTF8
    foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.md','README.md')) { Copy-Item -LiteralPath (Join-Path $workspace $name) -Destination $stage }

    $newFiles = Get-RelativeFiles $stage
    if (@($newFiles | Where-Object { $_ -ieq $manifestName }).Count -ne 0) { throw 'The staged package contains a reserved publisher manifest path.' }
    $oldManifestPath = Join-Path $output $manifestName
    $oldFiles = Read-OwnedFiles $output $oldManifestPath
    foreach ($relative in $newFiles) {
        $target = Get-SafeOwnedPath $output $relative
        if ((Test-Path -LiteralPath $target) -and $relative -notin $oldFiles) { throw "Output contains an unrelated file that conflicts with the package: $relative" }
    }
    foreach ($relative in @($newFiles + $oldFiles + $manifestName) | Sort-Object -Unique) {
        $target = Get-SafeOwnedPath $output $relative
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            try { $handle = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $handle.Dispose() }
            catch { throw "A published file is in use or not writable. Close Detroit Audio before publishing: $relative" }
        }
    }

    $ownership = [pscustomobject]@{version=1;files=@($newFiles)}
    $ownership | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage $manifestName) -Encoding UTF8
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    $backupFiles = @($oldFiles + $(if (Test-Path -LiteralPath $oldManifestPath -PathType Leaf) { @($manifestName) } else { @() })) | Sort-Object -Unique
    foreach ($relative in $backupFiles) {
        $source = Get-SafeOwnedPath $output $relative
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            $saved = Get-SafeOwnedPath $backup $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $saved
        }
    }
    if (!(Test-Path -LiteralPath $output -PathType Container)) { New-Item -ItemType Directory -Path $output -Force | Out-Null }
    try {
        foreach ($relative in $newFiles) {
            $target = Get-SafeOwnedPath $output $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath (Get-SafeOwnedPath $stage $relative) -Destination $target -Force
        }
        Copy-Item -LiteralPath (Join-Path $stage $manifestName) -Destination $oldManifestPath -Force
        foreach ($relative in $oldFiles | Where-Object { $_ -notin $newFiles }) {
            $stale = Get-SafeOwnedPath $output $relative
            if (Test-Path -LiteralPath $stale -PathType Leaf) { Remove-Item -LiteralPath $stale -Force }
        }
    } catch {
        $commitError = $_.Exception
        $rollbackErrors = @()
        foreach ($relative in @($newFiles + $manifestName) | Sort-Object -Unique) {
            try {
                $target = Get-SafeOwnedPath $output $relative
                if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force -ErrorAction Stop }
            } catch { $rollbackErrors += $_.Exception.Message }
        }
        foreach ($relative in $backupFiles) {
            try {
                $saved = Get-SafeOwnedPath $backup $relative
                if (Test-Path -LiteralPath $saved -PathType Leaf) {
                    $target = Get-SafeOwnedPath $output $relative
                    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                    Copy-Item -LiteralPath $saved -Destination $target -Force
                }
            } catch { $rollbackErrors += $_.Exception.Message }
        }
        if ($rollbackErrors.Count -gt 0) {
            $preserveBackup = $true
            throw ("Package update failed and rollback was incomplete. Recovery files were retained at '$backup'. Commit error: $($commitError.Message). Rollback errors: $($rollbackErrors -join '; ')")
        }
        throw $commitError
    }
    Write-Output ('Portable app: ' + (Join-Path $output $executableName))
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if (!$preserveBackup -and (Test-Path -LiteralPath $backup)) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
}
