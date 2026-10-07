$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$targets = Join-Path $workspace 'build/AutoPublish.targets'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('detroit-auto-publish-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$output = Join-Path $testRoot 'portable output'
$publisher = Join-Path $testRoot 'publisher.ps1'
$project = Join-Path $testRoot 'fixture.proj'
$report = Join-Path $output 'publish.json'
$previousFailure = $env:DETROITAUDIO_TEST_PUBLISH_FAIL
function Xml([string]$value) { [Security.SecurityElement]::Escape($value) }
try {
    @'
param([string]$Configuration, [string]$ToolsDirectory, [string]$OutputDirectory, [string]$PackagesPath)
$ErrorActionPreference = 'Stop'
if ($env:DETROITAUDIO_TEST_PUBLISH_FAIL -eq '1') { throw 'Synthetic publisher failure.' }
$hash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
[pscustomobject]@{Configuration=$Configuration;Tools=$ToolsDirectory;Packages=$PackagesPath;Sha256=$hash} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'publish.json')
'@ | Set-Content -LiteralPath $publisher -Encoding UTF8
    @"
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <AutoPublishScript>$(Xml $publisher)</AutoPublishScript>
    <AutoPublishOutputDirectory>$(Xml $output)</AutoPublishOutputDirectory>
    <AutoPublishToolsDirectory>$(Xml (Join-Path $testRoot 'offline tools'))</AutoPublishToolsDirectory>
    <NuGetPackageRoot>$(Xml ((Join-Path $testRoot 'package cache') + '\'))</NuGetPackageRoot>
  </PropertyGroup>
  <Import Project="$(Xml $targets)" Condition="Exists('$(Xml $targets)')" />
  <Target Name="Build">
    <Error Condition="'`$(FailBuild)' == 'true'" Text="Synthetic build failure." />
  </Target>
</Project>
"@ | Set-Content -LiteralPath $project -Encoding UTF8
    foreach ($configuration in @('Debug','Release')) {
        & dotnet msbuild $project -t:Build -nologo -v:quiet "-p:Configuration=$configuration"
        if ($LASTEXITCODE -ne 0) { throw "Fixture $configuration build failed." }
        if (!(Test-Path -LiteralPath $report -PathType Leaf)) { throw "Successful $configuration build did not run the automatic publisher." }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if ($result.Sha256 -ne (Get-FileHash -LiteralPath $publisher -Algorithm SHA256).Hash) { throw 'The publisher could not use its native hashing module.' }
        if ($result.Configuration -ne $configuration -or $result.Tools -ne (Join-Path $testRoot 'offline tools') -or
            $result.Packages.Replace('/','\').TrimEnd('\') -ne (Join-Path $testRoot 'package cache')) { throw 'Automatic publishing lost build configuration or paths with spaces.' }
        Remove-Item -LiteralPath $report
    }
    foreach ($property in @('AutoPublish=false','DesignTimeBuild=true')) {
        & dotnet msbuild $project -t:Build -nologo -v:quiet "-p:$property"
        if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $report)) { throw "Publishing was not suppressed by $property." }
    }
    & dotnet msbuild $project -t:Build -nologo -v:quiet -p:FailBuild=true
    if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $report)) { throw 'A failed build invoked the publisher.' }
    $env:DETROITAUDIO_TEST_PUBLISH_FAIL = '1'
    & dotnet msbuild $project -t:Build -nologo -v:quiet
    if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $report)) { throw 'A publisher failure was hidden by the build.' }
    Write-Output 'Automatic publishing checks passed: Debug/Release, spaced paths, design-time/explicit opt-out, build failure and publisher failure.'
} finally {
    $env:DETROITAUDIO_TEST_PUBLISH_FAIL = $previousFailure
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path -Leaf $resolved).StartsWith('detroit-auto-publish-')) { throw 'Invalid test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}
# Expected native failures were verified above; expose success to the Actions runner.
$global:LASTEXITCODE = 0
