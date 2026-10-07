$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$publishScript = Join-Path $PSScriptRoot 'Publish.ps1'
$lock = Get-Content -LiteralPath (Join-Path $workspace 'tools/components.lock.json') -Raw | ConvertFrom-Json
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('detroit-audio-publish-test-' + [Guid]::NewGuid().ToString('N'))
$toolRoot = Join-Path $testRoot 'tools'
$output = Join-Path $testRoot 'custom-output'
$global:PublishTestShouldFail = $false
$global:PublishTestExecutableNames = @('DetroitAudio.exe')

function global:dotnet {
    $arguments = @($args)
    $global:PublishTestArguments = $arguments
    if ($global:PublishTestShouldFail) { $global:LASTEXITCODE = 1; return }
    $outputIndex = [Array]::IndexOf($arguments, '-o')
    if ($outputIndex -lt 0 -or $outputIndex + 1 -ge $arguments.Count) { throw 'Publish did not provide a staging output path.' }
    $publishOutput = $arguments[$outputIndex + 1]
    New-Item -ItemType Directory -Path $publishOutput -Force | Out-Null
    foreach ($name in $global:PublishTestExecutableNames) {
        [IO.File]::WriteAllText((Join-Path $publishOutput $name), 'test package')
        [IO.File]::WriteAllText((Join-Path $publishOutput ([IO.Path]::GetFileNameWithoutExtension($name) + '.deps.json')), '{}')
        [IO.File]::WriteAllText((Join-Path $publishOutput ([IO.Path]::GetFileNameWithoutExtension($name) + '.runtimeconfig.json')), '{}')
    }
    $global:LASTEXITCODE = 0
}

try {
    foreach ($component in $lock.components) {
        $componentDirectory = Join-Path $toolRoot $component.name
        New-Item -ItemType Directory -Path $componentDirectory -Force | Out-Null
        foreach ($name in $component.files) { [IO.File]::WriteAllText((Join-Path $componentDirectory $name), ('fixture-' + $name)) }
    }
    $runtimeManifestPath = Join-Path $workspace 'src/DetroitAudio.Audio/Assets/install-tools.json'
    $runtimeManifestFixture = Join-Path $testRoot 'runtime-install-tools.json'
    $runtimeComponents = @()
    foreach ($component in $lock.components) {
        $runtimeFiles = @($component.files | ForEach-Object { [pscustomobject]@{Path=$_;Sha256=(Get-FileHash -LiteralPath (Join-Path $toolRoot ($component.name + '/' + $_)) -Algorithm SHA256).Hash} })
        $runtimeComponents += [pscustomobject]@{Name=$component.name;Version=$component.version;Files=$runtimeFiles}
    }
    [IO.File]::WriteAllText($runtimeManifestFixture, ($runtimeComponents | ConvertTo-Json -Depth 6))
    $global:PublishTestRuntimeManifestPath = [IO.Path]::GetFullPath($runtimeManifestPath)
    $global:PublishTestRuntimeManifestFixture = [IO.Path]::GetFullPath($runtimeManifestFixture)

    function global:Get-Content {
        param([string]$LiteralPath, [switch]$Raw)
        if ([IO.Path]::GetFullPath($LiteralPath) -ieq $global:PublishTestRuntimeManifestPath) {
            return [IO.File]::ReadAllText($global:PublishTestRuntimeManifestFixture)
        }
        Microsoft.PowerShell.Management\Get-Content @PSBoundParameters
    }

    $notices = Join-Path $toolRoot 'notices'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $notices 'vgmstream-ISC.txt'), 'offline notice')
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $unrelated = Join-Path $output 'user-data.txt'
    [IO.File]::WriteAllText($unrelated, 'keep this file')

    & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $output
    if ('-p:AutoPublish=false' -notin $global:PublishTestArguments) { throw 'The inner publish did not disable automatic publishing.' }
    $configurationIndex = [Array]::IndexOf($global:PublishTestArguments, '-c')
    if ($configurationIndex -lt 0 -or $global:PublishTestArguments[$configurationIndex + 1] -ne 'Release') { throw 'Manual publishing lost its Release default.' }
    & $publishScript -Configuration Debug -ToolsDirectory $toolRoot -OutputDirectory $output
    $configurationIndex = [Array]::IndexOf($global:PublishTestArguments, '-c')
    if ($configurationIndex -lt 0 -or $global:PublishTestArguments[$configurationIndex + 1] -ne 'Debug') { throw 'Publishing did not use the requested Debug configuration.' }
    if (!(Test-Path -LiteralPath (Join-Path $output 'DetroitAudio.exe') -PathType Leaf)) { throw 'Successful staging did not publish the application.' }
    if ((Get-Content -LiteralPath $unrelated -Raw) -ne 'keep this file') { throw 'Publishing changed an unrelated file in the custom output.' }
    $manifestPath = Join-Path $output '.detroit-audio-publish.json'
    $manifestBeforeFailure = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    $appBeforeFailure = (Get-FileHash -LiteralPath (Join-Path $output 'DetroitAudio.exe') -Algorithm SHA256).Hash

    $global:PublishTestShouldFail = $true
    $failedAsExpected = $false
    try { & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $output }
    catch { $failedAsExpected = $true }
    $global:PublishTestShouldFail = $false
    if (!$failedAsExpected) { throw 'The simulated staging failure was not reported.' }
    if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $manifestBeforeFailure) { throw 'A staging failure changed the published ownership manifest.' }
    if ((Get-FileHash -LiteralPath (Join-Path $output 'DetroitAudio.exe') -Algorithm SHA256).Hash -ne $appBeforeFailure) { throw 'A staging failure changed the published application.' }
    if ((Get-Content -LiteralPath $unrelated -Raw) -ne 'keep this file') { throw 'A staging failure changed an unrelated file.' }

    $corruptTool = Join-Path $toolRoot 'vgmstream/vgmstream-cli.exe'
    $originalToolContents = [IO.File]::ReadAllText($corruptTool)
    [IO.File]::WriteAllText($corruptTool, 'corrupted tool')
    $corruptionFailed = $false
    try { & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $output }
    catch { $corruptionFailed = $_.Exception.Message -like '*Tool checksum mismatch*' }
    finally { [IO.File]::WriteAllText($corruptTool, $originalToolContents) }
    if (!$corruptionFailed) { throw 'A tool that failed the runtime manifest checksum was not rejected.' }
    if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $manifestBeforeFailure) { throw 'Corrupt local tools changed the published ownership manifest.' }
    if ((Get-FileHash -LiteralPath (Join-Path $output 'DetroitAudio.exe') -Algorithm SHA256).Hash -ne $appBeforeFailure) { throw 'Corrupt local tools changed the published application.' }

    $legacyOutput = Join-Path $testRoot 'legacy-output'
    $legacyManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($relative in $legacyManifest.files) {
        $source = Join-Path $output ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
        $destination = Join-Path $legacyOutput ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $source -Destination $destination
    }
    $unrelatedDll = Join-Path $legacyOutput 'unrelated.dll'
    [IO.File]::WriteAllText($unrelatedDll, 'keep this unrelated library')
    & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $legacyOutput
    if ((Get-Content -LiteralPath $unrelatedDll -Raw) -ne 'keep this unrelated library') { throw 'Legacy package adoption changed an unrelated DLL.' }
    $legacyOwnership = Get-Content -LiteralPath (Join-Path $legacyOutput '.detroit-audio-publish.json') -Raw | ConvertFrom-Json
    if ($legacyOwnership.files -contains 'tools/vgmstream/unrelated.dll') { throw 'Legacy package adoption claimed an unrelated DLL.' }

    $collisionOutput = Join-Path $testRoot 'collision-output'
    New-Item -ItemType Directory -Path $collisionOutput -Force | Out-Null
    $collisionExe = Join-Path $collisionOutput 'DetroitAudio.exe'
    [IO.File]::WriteAllText($collisionExe, 'user-owned executable')
    $collisionFailed = $false
    try { & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $collisionOutput }
    catch { $collisionFailed = $_.Exception.Message -like '*conflicts with the package*' }
    if (!$collisionFailed -or (Get-Content -LiteralPath $collisionExe -Raw) -ne 'user-owned executable') { throw 'An unrelated filename collision was overwritten or not rejected.' }

    $global:PublishTestOutput = [IO.Path]::GetFullPath($output)
    $lockedExe = [IO.File]::Open((Join-Path $output 'DetroitAudio.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $beforeLockedManifest = [IO.File]::ReadAllText((Join-Path $output '.detroit-audio-publish.json'))
        $lockRejected = $false
        try { & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $output }
        catch { $lockRejected = $_.Exception.Message -like '*in use or not writable*' }
        if (!$lockRejected -or [IO.File]::ReadAllText((Join-Path $output '.detroit-audio-publish.json')) -ne $beforeLockedManifest) { throw 'Locked output was not rejected before changing the package.' }
    } finally { $lockedExe.Dispose() }
    $global:PublishTestCommitCopies = 0
    $global:PublishTestFailCommit = $true
    $global:PublishTestFailRestore = $false
    function global:Copy-Item {
        param([string]$LiteralPath, [string]$Destination, [switch]$Recurse, [switch]$Force)
        $destinationPath = [IO.Path]::GetFullPath($Destination)
        if ($destinationPath.StartsWith($global:PublishTestOutput + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            $global:PublishTestCommitCopies++
            if ($global:PublishTestCommitCopies -eq 2) { throw 'Simulated commit failure.' }
            if ($global:PublishTestFailRestore -and $LiteralPath.Contains('.backup-')) { throw 'Simulated restoration failure.' }
        }
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    }
    $commitFailedAsExpected = $false
    $commitFailureMessage = ''
    try { & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $output }
    catch { $commitFailureMessage = $_.Exception.Message; $commitFailedAsExpected = $commitFailureMessage -eq 'Simulated commit failure.' }
    finally { $global:PublishTestFailCommit = $false }
    if (!$commitFailedAsExpected) { throw ('The simulated package commit failure was not reported: ' + $commitFailureMessage) }
    if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $manifestBeforeFailure) { throw 'A failed package commit did not restore the previous ownership manifest.' }
    if ((Get-FileHash -LiteralPath (Join-Path $output 'DetroitAudio.exe') -Algorithm SHA256).Hash -ne $appBeforeFailure) { throw 'A failed package commit did not restore the previous application.' }
    if ((Get-Content -LiteralPath $unrelated -Raw) -ne 'keep this file') { throw 'A failed package commit changed an unrelated file.' }

    $global:PublishTestCommitCopies = 0
    $global:PublishTestFailRestore = $true
    $global:PublishTestFailCommit = $true
    $restoreFailureMessage = ''
    try { & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $output }
    catch { $restoreFailureMessage = $_.Exception.Message }
    finally { Remove-Item Function:\Copy-Item -ErrorAction SilentlyContinue; $global:PublishTestFailCommit = $false }
    if ($restoreFailureMessage -notmatch "Recovery files were retained at '([^']+)'") { throw ('An incomplete rollback did not report a retained recovery directory: ' + $restoreFailureMessage) }
    $recoveryDirectory = $Matches[1]
    if (!(Test-Path -LiteralPath (Join-Path $recoveryDirectory 'DetroitAudio.exe') -PathType Leaf)) { throw 'An incomplete rollback did not retain the prior application backup.' }

    $renameOutput = Join-Path $testRoot 'rename-output'
    & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $renameOutput | Out-Null
    $unrelatedExe = Join-Path $renameOutput 'UserTool.exe'
    [IO.File]::WriteAllText($unrelatedExe, 'keep this executable')
    $global:PublishTestExecutableNames = @('Detroit Audio Extractor.exe')
    $renameMessages = @(& $publishScript -ToolsDirectory $toolRoot -OutputDirectory $renameOutput)
    $renamedExe = Join-Path $renameOutput 'Detroit Audio Extractor.exe'
    if (!(Test-Path -LiteralPath $renamedExe -PathType Leaf) -or (Test-Path -LiteralPath (Join-Path $renameOutput 'DetroitAudio.exe'))) { throw 'Publishing a renamed executable did not replace the previous owned executable.' }
    if (('Portable app: ' + $renamedExe) -notin $renameMessages) { throw 'Publishing did not report the renamed executable path.' }
    if ([IO.File]::ReadAllText($unrelatedExe) -ne 'keep this executable') { throw 'Renaming the package changed an unrelated executable.' }
    $renamedOwnership = Get-Content -LiteralPath (Join-Path $renameOutput '.detroit-audio-publish.json') -Raw | ConvertFrom-Json
    if ('Detroit Audio Extractor.exe' -notin $renamedOwnership.files -or 'DetroitAudio.exe' -in $renamedOwnership.files) { throw 'The ownership manifest did not follow the executable rename.' }
    if ($renamedOwnership.files | Where-Object { $_ -match '\.(deps|runtimeconfig)\.json$' }) { throw 'Single-file publishing retained loose application metadata.' }

    Remove-Item -LiteralPath (Join-Path $renameOutput '.detroit-audio-publish.json')
    $unknownApplication = Join-Path $renameOutput 'UnknownApplication.exe'
    Move-Item -LiteralPath $renamedExe -Destination $unknownApplication
    $unknownLegacyRejected = $false
    try { & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $renameOutput | Out-Null }
    catch { $unknownLegacyRejected = $_.Exception.Message -like '*conflicts with the package*' }
    if (!$unknownLegacyRejected -or !(Test-Path -LiteralPath $unknownApplication -PathType Leaf)) { throw 'Legacy package adoption accepted an unsupported application executable.' }
    Move-Item -LiteralPath $unknownApplication -Destination $renamedExe
    $global:PublishTestExecutableNames = @('DetroitAudio.exe')
    & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $renameOutput | Out-Null
    if (Test-Path -LiteralPath $renamedExe) { throw 'Legacy package adoption did not remove the previous supported executable name.' }
    if ([IO.File]::ReadAllText($unrelatedExe) -ne 'keep this executable') { throw 'Legacy package adoption claimed an unrelated executable.' }
    $global:PublishTestExecutableNames = @('Detroit Audio Extractor.exe')
    & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $renameOutput | Out-Null

    foreach ($executableNames in @(@(), @('One.exe', 'Two.exe'))) {
        $global:PublishTestExecutableNames = $executableNames
        $invalidExecutableCountRejected = $false
        try { & $publishScript -ToolsDirectory $toolRoot -OutputDirectory $renameOutput | Out-Null }
        catch { $invalidExecutableCountRejected = $_.Exception.Message -like '*exactly one root executable*' }
        if (!$invalidExecutableCountRejected) { throw 'Publishing did not reject a staged package without exactly one root executable.' }
    }
    $global:PublishTestExecutableNames = @('Detroit Audio Extractor.exe')

    function global:git { $global:LASTEXITCODE = 0; return 'LICENSE' }
    $packageOutput = Join-Path $testRoot 'packages'
    & (Join-Path $PSScriptRoot 'Package.ps1') -BuildDirectory $renameOutput -OutputDirectory $packageOutput | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $packageOutput 'DetroitAudio-Windows-x64-Updated.zip'))
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        if ('Detroit Audio Extractor.exe' -notin $entries -or 'DetroitAudio.exe' -in $entries -or 'UserTool.exe' -in $entries) { throw 'Packaging did not use the current owned application executable.' }
        if (!($entries | Where-Object { $_ -like 'tools/*' })) { throw 'Packaging dropped the offline tools.' }
    } finally { $archive.Dispose() }
    $renameManifestPath = Join-Path $renameOutput '.detroit-audio-publish.json'
    $renameManifest = [IO.File]::ReadAllText($renameManifestPath)
    foreach ($applicationFiles in @(@(), @('One.exe', 'Two.exe'))) {
        $invalidOwnership = $renameManifest | ConvertFrom-Json
        $invalidOwnership.files = @($invalidOwnership.files | Where-Object { $_ -notmatch '^[^/\\]+\.exe$' }) + $applicationFiles
        [IO.File]::WriteAllText($renameManifestPath, ($invalidOwnership | ConvertTo-Json -Depth 4))
        $invalidPackageRejected = $false
        try { & (Join-Path $PSScriptRoot 'Package.ps1') -BuildDirectory $renameOutput -OutputDirectory $packageOutput | Out-Null }
        catch { $invalidPackageRejected = $_.Exception.Message -like '*exactly one root executable*' }
        if (!$invalidPackageRejected) { throw 'Packaging did not reject an ownership manifest without exactly one root executable.' }
    }
    [IO.File]::WriteAllText($renameManifestPath, $renameManifest)

    Write-Output 'Publisher checks passed: tool hashes, staging failures, file ownership, executable renames, legacy adoption, collisions, locked files, rollback, and portable packaging.'
} finally {
    Remove-Item Function:\dotnet -ErrorAction SilentlyContinue
    Remove-Item Function:\Copy-Item -ErrorAction SilentlyContinue
    Remove-Item Function:\Get-Content -ErrorAction SilentlyContinue
    Remove-Item Function:\git -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Variable PublishTestShouldFail, PublishTestArguments, PublishTestExecutableNames -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable PublishTestOutput, PublishTestCommitCopies, PublishTestFailCommit, PublishTestFailRestore, PublishTestRuntimeManifestPath, PublishTestRuntimeManifestFixture -Scope Global -ErrorAction SilentlyContinue
}
