param(
    [string]$Sqpack,
    [string]$OriginalSamples
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'artifacts/validation/home'
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
function Invoke-DotnetCheck([string]$Name, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $taskOutput "$Name.log") | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE). See artifacts/validation/home/$Name.log" }
}
Push-Location $taskRoot
try {
    Invoke-DotnetCheck 'build-plugin' @('build', 'src/AetherVfx.Plugin/AetherVfx.Plugin.csproj', '-c', 'Release')
    $taskTestArgs = @('run', '--project', 'tests/AetherVfx.Tests', '-c', 'Release')
    if ($OriginalSamples) { $taskTestArgs += @('--', (Resolve-Path -LiteralPath $OriginalSamples).Path) }
    Invoke-DotnetCheck 'tests' $taskTestArgs
    Invoke-DotnetCheck 'build-cli' @('build', 'tools/AetherVfx.Cli/AetherVfx.Cli.csproj', '-c', 'Release')
    $taskCli = Join-Path $taskRoot 'tools/AetherVfx.Cli/bin/Release/net10.0/AetherVfx.Cli.dll'
    if ($Sqpack) {
        $Sqpack = (Resolve-Path -LiteralPath $Sqpack).Path
        Invoke-DotnetCheck 'probe' @($taskCli, 'probe', $Sqpack, 'vfx/common/eff/dk10ht_wra0c.avfx')
        $taskSamples = Join-Path $taskRoot 'artifacts/samples'
        Invoke-DotnetCheck 'demo' @($taskCli, 'demo', $Sqpack, $taskSamples)
        foreach ($taskKind in @('Equipment', 'Weapon', 'Accessory')) {
            Invoke-DotnetCheck "verify-$taskKind" @($taskCli, 'verify', (Join-Path $taskSamples "$taskKind.pmp"))
        }
        Invoke-DotnetCheck 'recompile' @($taskCli, 'compile', (Join-Path $taskSamples 'Equipment.aethervfx.json'), $Sqpack, (Join-Path $taskSamples 'recompiled.pmp'))
        Invoke-DotnetCheck 'verify-recompiled' @($taskCli, 'verify', (Join-Path $taskSamples 'recompiled.pmp'))
    }
    $taskPlugin = Join-Path $taskRoot 'artifacts/plugin'
    New-Item -ItemType Directory -Force -Path $taskPlugin | Out-Null
    Copy-Item -Path 'src/AetherVfx.Plugin/bin/Release/*' -Destination $taskPlugin -Recurse -Force
    $taskDependencies = if ($env:DALAMUD_HOME) { $env:DALAMUD_HOME } else { Join-Path $env:APPDATA 'XIVLauncherCN/addon/Hooks/dev' }
    $taskInventory = foreach ($taskName in @('Dalamud.dll', 'FFXIVClientStructs.dll', 'Lumina.dll', 'Lumina.Excel.dll')) {
        $taskFile = Get-Item -LiteralPath (Join-Path $taskDependencies $taskName)
        [ordered]@{
            Name = $taskName
            AssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($taskFile.FullName).Version.ToString()
            FileVersion = $taskFile.VersionInfo.FileVersion
            SHA256 = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash
        }
    }
    [ordered]@{
        Time = [DateTimeOffset]::Now.ToString('o')
        Commit = (& git rev-parse HEAD)
        Branch = (& git branch --show-current)
        Dirty = [bool](& git status --porcelain)
        DotnetSdk = (& dotnet --version)
        Dependencies = @($taskInventory)
        Sqpack = $Sqpack
        OriginalSamples = $OriginalSamples
        PluginSHA256 = (Get-FileHash -LiteralPath (Join-Path $taskPlugin 'AetherVfx.dll') -Algorithm SHA256).Hash
        NativeCompatibility = 'Unverified; allocation and playback disabled'
        VisualAcceptance = 'PendingGameValidation'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'environment.json') -Encoding utf8
    Get-ChildItem -LiteralPath $taskPlugin -File -Recurse | Get-FileHash -Algorithm SHA256 |
        Select-Object Path, Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'build-hashes.json') -Encoding utf8
    Write-Host 'Development verification complete. Native preview remains disabled; no game was started.'
}
finally { Pop-Location }
