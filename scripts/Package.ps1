param([string]$BuildDirectory = 'artifacts/DetroitAudio', [string]$OutputDirectory = 'artifacts/packages')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
function Resolve-Root([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $workspace $Path))
}
function Resolve-Member([string]$Root, [string]$Member) {
    if ([IO.Path]::IsPathRooted($Member) -or $Member.Contains(':') -or ($Member -split '[\\/]' | Where-Object { $_ -in @('', '.', '..') })) { throw "Invalid package member: $Member" }
    $prefix = $Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Member))
    if (!$path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Package member escapes its root: $Member" }
    $current = $path
    while ($current -and $current.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $current) -and ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked paths cannot be packaged: $Member" }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $path
}
function Write-Package([string]$Root, [string[]]$Members, [string]$Name) {
    $destination = Join-Path $output $Name
    $temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $file = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew)
        $archive = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($member in $Members) {
                $path = Resolve-Member $Root $member
                if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package member is missing: $member" }
                $entry = $archive.CreateEntry($member.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
                $source = [IO.File]::OpenRead($path); $target = $entry.Open()
                try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }
            }
        } finally { $archive.Dispose() }
        $check = [IO.Compression.ZipFile]::OpenRead($temporary)
        try { if ($check.Entries.Count -ne $Members.Count) { throw 'Package entry count mismatch.' } }
        finally { $check.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $destination -Force
        [pscustomobject]@{File=$destination;Entries=$Members.Count;Bytes=(Get-Item -LiteralPath $destination).Length;SHA256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash}
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$build = Resolve-Root $BuildDirectory; $output = Resolve-Root $OutputDirectory
New-Item -ItemType Directory -Path $output -Force | Out-Null
$ownership = Get-Content -LiteralPath (Join-Path $build '.detroit-audio-publish.json') -Raw | ConvertFrom-Json
if ($ownership.version -ne 1 -or $ownership.files -isnot [System.Array]) { throw 'Publish a managed portable build before packaging.' }
$buildMembers = @($ownership.files + '.detroit-audio-publish.json' | Sort-Object -Unique)
$executables = @($buildMembers | Where-Object { $_ -notmatch '[/\\]' -and $_ -match '\.exe$' })
if ($executables.Count -ne 1) { throw 'The application package must contain exactly one root executable.' }
if (!($buildMembers | Where-Object { $_ -like 'tools/*' })) { throw 'Offline tools are required in the portable package.' }
if ($buildMembers | Where-Object { $_ -notmatch '[/\\]' -and $_ -match '\.(dll|pdb)$' }) { throw 'The application package contains loose DLL/PDB files.' }
$raw = (& git -C $workspace -c core.quotepath=false ls-files --cached --others --exclude-standard -z) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Could not inventory source files.' }
$sourceMembers = @($raw.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | Where-Object {
    $_ -notmatch '(^|/)(\.git|\.local|artifacts|bin|obj|TestResults|\.vs)(/|$)' -and $_ -notmatch '\.(user|db|db-shm|db-wal)$'
} | Sort-Object -Unique)
Write-Package $workspace $sourceMembers 'DetroitAudio-Source-Updated.zip'
Write-Package $build $buildMembers 'DetroitAudio-Windows-x64-Updated.zip'
