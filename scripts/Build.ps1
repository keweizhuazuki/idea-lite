param(
    [ValidateSet('Both', 'Framework', 'Portable')][string]$Package = 'Both',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repositoryPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (Test-Path -LiteralPath 'D:\Apps\dotnet\dotnet.exe') {
    $dotnetPath = 'D:\Apps\dotnet\dotnet.exe'
    $env:DOTNET_ROOT = 'D:\Apps\dotnet'
} else { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
if (Test-Path -LiteralPath 'D:\') {
    $cachePath = 'D:\Codex\cache'
    $temporaryPath = 'D:\Codex\work\idea-lite'
    New-Item -ItemType Directory -Force $temporaryPath,(Join-Path $cachePath 'dotnet'),(Join-Path $cachePath 'nuget') | Out-Null
    $env:DOTNET_CLI_HOME = Join-Path $cachePath 'dotnet'
    $env:NUGET_PACKAGES = Join-Path $cachePath 'nuget'
    $env:TEMP = $temporaryPath
    $env:TMP = $temporaryPath
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
function Invoke-Dotnet {
    & $dotnetPath @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Push-Location -LiteralPath $repositoryPath
try {
    Invoke-Dotnet restore IdeaLite.slnx --locked-mode
    Invoke-Dotnet build IdeaLite.slnx -c Release --no-restore --nologo
    if (-not $SkipTests) { Invoke-Dotnet run --project tests/IdeaLite.Tests -c Release --no-build }
    $artifactsPath = Join-Path $repositoryPath 'artifacts'
    New-Item -ItemType Directory -Force $artifactsPath | Out-Null
    $modes = if ($Package -eq 'Both') { @('Framework','Portable') } else { @($Package) }
    $checksums = @()
    foreach ($mode in $modes) {
        $name = 'IdeaLite-win-x64-' + $mode.ToLowerInvariant()
        $outputPath = Join-Path $artifactsPath $name
        $selfContained = if ($mode -eq 'Portable') { 'true' } else { 'false' }
        Invoke-Dotnet publish src/IdeaLite/IdeaLite.csproj -c Release -r win-x64 --self-contained $selfContained -o $outputPath '-p:DebugType=None' '-p:DebugSymbols=false'
        Copy-Item -LiteralPath (Join-Path $repositoryPath 'LICENSE'),(Join-Path $repositoryPath 'THIRD-PARTY-NOTICES.md'),(Join-Path $repositoryPath 'README.md') -Destination $outputPath -Force
        Copy-Item -LiteralPath (Join-Path $repositoryPath 'licenses') -Destination $outputPath -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $repositoryPath 'docs') -Destination $outputPath -Recurse -Force
        $archivePath = Join-Path $artifactsPath ($name + '.zip')
        Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $archivePath -CompressionLevel Optimal -Force
        $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksums += "$hash  $name.zip"
        Write-Output "Package: $archivePath"
    }
    [IO.File]::WriteAllLines((Join-Path $artifactsPath 'SHA256SUMS.txt'), $checksums, (New-Object Text.UTF8Encoding $false))
} finally { Pop-Location }
