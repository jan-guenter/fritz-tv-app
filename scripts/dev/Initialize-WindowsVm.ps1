#requires -Version 5.1
<#
.SYNOPSIS
    Installs and verifies the pinned native Windows development environment.
.DESCRIPTION
    Run elevated as the same user who owns the interactive VirtualBox console.
    The host must take a VM snapshot first. No Windows autologon is configured.
    Machine-local credentials and reports are stored outside the repository.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'WorkspaceRoot', Justification = 'This script-scoped option is consumed by Write-Verification and runner helpers; the analyzer does not traverse those helper scopes.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'AllowedRtpSources', Justification = 'This script-scoped option is consumed by Set-RtpFirewall; the analyzer does not traverse those helper scopes.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'McpPort', Justification = 'This script-scoped option is consumed by Set-Mcp and Write-Verification; the analyzer does not traverse those helper scopes.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'RunnerTokenPath', Justification = 'This script-scoped option is consumed by Set-InteractiveRunner; the analyzer does not traverse those helper scopes.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'RunnerDirectory', Justification = 'This script-scoped option is consumed by runner configuration helpers; the analyzer does not traverse those helper scopes.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'RepositoryUrl', Justification = 'This script-scoped option is consumed by Set-InteractiveRunner; the analyzer does not traverse those helper scopes.')]
[CmdletBinding()]
param(
    [string]$ConfigurationPath,
    [string]$StateDirectory = (Join-Path $env:LOCALAPPDATA 'FritzTVDevelopment'),
    [string]$WorkspaceRoot = 'C:\src\fritz-tv-app',
    [string[]]$AllowedRtpSources = @('192.168.0.1', '10.0.2.2'),
    [ValidateRange(1024, 65535)][int]$McpPort = 8000,
    [switch]$SkipPackages,
    [switch]$SkipMcp,
    [switch]$SkipFirewall,
    [switch]$VerifyOnly,
    [switch]$ConfigureRunner,
    [string]$RunnerTokenPath,
    [string]$RunnerDirectory = 'C:\actions-runner-fritz-tv',
    [string]$RepositoryUrl = 'https://github.com/jan-guenter/fritz-tv-app'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
if (-not $ConfigurationPath) {
    $scriptDirectory = $PSScriptRoot
    if (-not $scriptDirectory -and $MyInvocation.MyCommand.Path) { $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path }
    if (-not $scriptDirectory) { throw 'Cannot resolve the script directory; supply an absolute ConfigurationPath.' }
    $ConfigurationPath = Join-Path $scriptDirectory 'windows-toolchain.json'
}
$configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
if ($configuration.schemaVersion -ne 1) { throw 'Unsupported Windows toolchain schema.' }
$utf8 = New-Object Text.UTF8Encoding($false)

function Update-ProcessPath {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Refreshes only this process environment so verification resolves installed tools; it makes no persistent machine or user changes.')]
    param()
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $additional = @(
        (Join-Path $env:USERPROFILE '.local\bin'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links'),
        (Join-Path $env:ProgramFiles 'dotnet'),
        (Join-Path $env:ProgramFiles 'PowerShell\7'),
        (Join-Path $env:ProgramFiles 'Git\cmd'),
        (Join-Path $env:ProgramFiles 'MKVToolNix'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6')
    )
    $env:Path = (@($machine, $user) + $additional) -join ';'
}

function Find-Tool([string]$Name, [string[]]$Candidates = @()) {
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    foreach ($candidate in $Candidates) { if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate } }
    return $null
}

function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw ('Native command failed with exit code {0}: {1}' -f $code, $FilePath) }
}

function Protect-LocalFile([string]$Path) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($sid)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'Allow')))
    $systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($systemSid, 'FullControl', 'Allow')))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Install-WinGetPackage([object]$Package) {
    $winget = Find-Tool 'winget.exe'
    if (-not $winget) { throw 'WinGet/App Installer must be available for the interactive user.' }
    Write-Information ('Installing/verifying {0} {1}' -f $Package.id, $Package.version) -InformationAction Continue
    $arguments = @('install', '--id', $Package.id, '--exact', '--version', $Package.version,
        '--source', 'winget', '--silent', '--accept-package-agreements',
        '--accept-source-agreements', '--disable-interactivity')
    & $winget @arguments
    $code = $LASTEXITCODE
    # WinGet verifies the installer SHA-256 from its pinned community manifest.
    # 0x8A15002B means the installed package has no applicable upgrade.
    $unsigned = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$code), 0)
    if ($code -ne 0 -and $code -ne 3010 -and $unsigned -ne [uint32]2316632107) {
        throw ('WinGet failed installing {0}; exit {1} (0x{2:X8}).' -f $Package.id, $code, $unsigned)
    }
    if ($code -eq 3010) { Write-Warning 'An installer requested a restart. Reconnect and rerun after restarting.' }
    Update-ProcessPath
}

function Set-RtpFirewall {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Private Windows bootstrap phase; the explicit top-level setup branch authorizes changes and VerifyOnly never calls this configuration helper.')]
    param()
    $name = 'FritzTvVmRtp'
    $existing = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
    if ($existing) {
        Set-NetFirewallRule -Name $name -Enabled True -Direction Inbound -Action Allow -Profile Any | Out-Null
        $existing | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol UDP -LocalPort '50000-50031' | Out-Null
        $existing | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress $AllowedRtpSources | Out-Null
    } else {
        New-NetFirewallRule -Name $name -DisplayName 'FRITZ TV development RTP/RTCP' -Group 'FRITZ TV Development' `
            -Enabled True -Direction Inbound -Action Allow -Profile Any -Protocol UDP `
            -LocalPort '50000-50031' -RemoteAddress $AllowedRtpSources | Out-Null
    }
}

function Set-Mcp {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Private Windows bootstrap phase; the explicit top-level setup branch authorizes changes and VerifyOnly never calls this configuration helper.')]
    param()
    $uv = Find-Tool 'uv.exe'
    if (-not $uv) { throw 'uv was not found after installation.' }
    Invoke-Native $uv @('python', 'install', $configuration.pythonVersion)
    $python = (& $uv python find $configuration.pythonVersion | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $python)) { throw 'The pinned Python interpreter could not be resolved.' }
    Invoke-Native $uv @('tool', 'install', '--python', $python, ('windows-mcp==' + $configuration.windowsMcpVersion))
    Update-ProcessPath
    $toolBin = (& $uv tool dir --bin | Out-String).Trim()
    $mcp = Join-Path $toolBin 'windows-mcp.exe'
    if (-not (Test-Path -LiteralPath $mcp)) { throw 'windows-mcp.exe was not installed.' }

    $configDirectory = Join-Path $env:USERPROFILE '.windows-mcp'
    $configPath = Join-Path $configDirectory 'config.toml'
    $secretPath = Join-Path $StateDirectory 'windows-mcp-token.txt'
    New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $secretPath) {
        $secret = (Get-Content -LiteralPath $secretPath -Raw).Trim()
    } else {
        $bytes = New-Object byte[] 32
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        $secret = [Convert]::ToBase64String($bytes)
        [IO.File]::WriteAllText($secretPath, $secret, $utf8)
    }
    if ([Convert]::FromBase64String($secret).Length -lt 32) { throw 'The stored MCP authentication key is invalid.' }
    Protect-LocalFile $secretPath
    $config = @"
# Managed by FRITZ TV's Windows VM bootstrap. Contains a machine-local secret.
[server]
transport = "streamable-http"
host = "127.0.0.1"
port = $McpPort
auth_key = "$secret"
"@
    $changed = -not (Test-Path -LiteralPath $configPath)
    if (-not $changed) { $changed = (Get-Content -LiteralPath $configPath -Raw) -ne $config }
    if ($changed) {
        if (Test-Path -LiteralPath $configPath) { Copy-Item -LiteralPath $configPath -Destination ($configPath + '.previous') -Force }
        [IO.File]::WriteAllText($configPath, $config, $utf8)
    }
    Protect-LocalFile $configPath
    if (Test-Path -LiteralPath ($configPath + '.previous')) { Protect-LocalFile ($configPath + '.previous') }
    Invoke-Native $mcp @('install', '--transport', 'streamable-http', '--host', '127.0.0.1', '--port', [string]$McpPort)
    $mcpSettings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
        -RestartCount 3 -RestartInterval ([TimeSpan]::FromMinutes(1))
    Set-ScheduledTask -TaskName 'windows-mcp-server' -Settings $mcpSettings | Out-Null
    $task = Get-ScheduledTask -TaskName 'windows-mcp-server' -ErrorAction Stop
    if ($changed -and $task.State -eq 'Running') { Stop-ScheduledTask -TaskName $task.TaskName; $task = Get-ScheduledTask -TaskName $task.TaskName }
    if ($task.State -ne 'Running') { Start-ScheduledTask -TaskName $task.TaskName }
    Write-Information ('MCP configured on guest loopback port {0}. Token is stored locally; it is not printed.' -f $McpPort) -InformationAction Continue
}

function Set-RunnerAdmissionGuard {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Private Windows bootstrap phase; the explicit top-level setup branch authorizes changes and VerifyOnly never calls this configuration helper.')]
    param()
    $source = Join-Path $PSScriptRoot 'Assert-TrustedWindowsJob.ps1'
    $tests = Join-Path $PSScriptRoot 'Test-TrustedWindowsJob.ps1'
    if (-not (Test-Path -LiteralPath $source) -or -not (Test-Path -LiteralPath $tests)) {
        throw 'Runner setup requires both trusted-job guard scripts beside the bootstrap script.'
    }
    $shell = Find-Tool 'pwsh.exe'
    if (-not $shell) { throw 'PowerShell 7 is required before configuring the guarded runner.' }
    & $shell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $tests -GuardPath $source
    if ($LASTEXITCODE -ne 0) { throw 'Runner admission policy tests failed; runner was not started.' }
    $guardDirectory = Join-Path $StateDirectory 'runner-hooks'
    New-Item -ItemType Directory -Path $guardDirectory -Force | Out-Null
    $target = Join-Path $guardDirectory 'Assert-TrustedWindowsJob.ps1'
    Copy-Item -LiteralPath $source -Destination $target -Force
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
        throw 'Installed runner guard hash mismatch.'
    }
    Protect-LocalFile $target
    $environmentPath = Join-Path $RunnerDirectory '.env'
    $lines = @()
    if (Test-Path -LiteralPath $environmentPath) {
        $lines = @(Get-Content -LiteralPath $environmentPath | Where-Object { $_ -notmatch '^(ACTIONS_RUNNER_HOOK_JOB_STARTED|PSExecutionPolicyPreference)=' })
    }
    $lines += 'ACTIONS_RUNNER_HOOK_JOB_STARTED=' + $target
    $lines += 'PSExecutionPolicyPreference=RemoteSigned'
    [IO.File]::WriteAllLines($environmentPath, [string[]]$lines, $utf8)
    Protect-LocalFile $environmentPath
}

function Set-InteractiveRunner {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Private Windows bootstrap phase; the explicit top-level setup branch authorizes changes and VerifyOnly never calls this configuration helper.')]
    param()
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if (Get-Process -Name 'Runner.Worker' -ErrorAction SilentlyContinue) {
        throw 'A runner job is active; finish it before reconfiguring runner admission.'
    }
    $existingTask = Get-ScheduledTask -TaskName 'FritzTV-GitHubRunner' -ErrorAction SilentlyContinue
    if ($existingTask -and $existingTask.State -eq 'Running') { Stop-ScheduledTask -TaskName $existingTask.TaskName }
    Get-CimInstance Win32_Process -Filter "Name='Runner.Listener.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($RunnerDirectory + '\', [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if (-not (Test-Path -LiteralPath (Join-Path $RunnerDirectory '.runner'))) {
        if (-not $RunnerTokenPath -or -not (Test-Path -LiteralPath $RunnerTokenPath)) {
            throw 'ConfigureRunner requires a machine-local RunnerTokenPath containing a short-lived repository registration token.'
        }
        $runner = $configuration.runner
        $archive = Join-Path $StateDirectory ('actions-runner-win-x64-' + $runner.version + '.zip')
        if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -UseBasicParsing -Uri $runner.url -OutFile $archive }
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $runner.sha256) { throw 'GitHub Actions runner archive SHA-256 mismatch.' }
        New-Item -ItemType Directory -Path $RunnerDirectory -Force | Out-Null
        Expand-Archive -LiteralPath $archive -DestinationPath $RunnerDirectory -Force
        $token = (Get-Content -LiteralPath $RunnerTokenPath -Raw).Trim()
        Push-Location $RunnerDirectory
        try {
            & (Join-Path $RunnerDirectory 'config.cmd') --unattended --url $RepositoryUrl --token $token `
                --name 'fritz-tv-win11' --labels 'fritz-tv-win11' --work '_work' --replace
            if ($LASTEXITCODE -ne 0) { throw 'GitHub runner registration failed.' }
        } finally { Pop-Location; $token = $null }
    }
    Set-RunnerAdmissionGuard
    $taskName = 'FritzTV-GitHubRunner'
    $action = New-ScheduledTaskAction -Execute $env:ComSpec -Argument ('/d /c ""{0}""' -f (Join-Path $RunnerDirectory 'run.cmd')) -WorkingDirectory $RunnerDirectory
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
    $principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -RestartCount 3 -RestartInterval ([TimeSpan]::FromMinutes(1)) -StartWhenAvailable `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    if ((Get-ScheduledTask -TaskName $taskName).State -ne 'Running') { Start-ScheduledTask -TaskName $taskName }
}

function Get-ToolResult([string]$Name, [string]$Path, [string[]]$Arguments, [string]$VersionText = $null) {
    if (-not $Path) { return [ordered]@{ Name = $Name; Installed = $false; Path = $null; Version = $null; Signature = $null } }
    $text = $VersionText
    if (-not $text) {
        try { $text = (& $Path @Arguments 2>&1 | Out-String).Trim() }
        catch { $text = 'Version probe failed: ' + $_.Exception.Message }
    }
    $signatureStatus = 'Unavailable'; $signer = $null; $signatureError = $null
    try {
        $signature = Get-AuthenticodeSignature -LiteralPath $Path
        $signatureStatus = [string]$signature.Status
        if ($signature.SignerCertificate) { $signer = $signature.SignerCertificate.Subject }
    } catch { $signatureError = $_.Exception.Message }
    return [ordered]@{ Name = $Name; Installed = $true; Path = $Path; Version = $text;
        Signature = $signatureStatus; Signer = $signer; SignatureError = $signatureError }
}

function Write-VerificationReport {
    Update-ProcessPath
    $tools = @()
    $tools += Get-ToolResult 'PowerShell' (Find-Tool 'pwsh.exe') @('-NoLogo', '-NoProfile', '-Command', '$PSVersionTable.PSVersion.ToString()')
    $tools += Get-ToolResult 'Git' (Find-Tool 'git.exe') @('--version')
    $tools += Get-ToolResult 'DotNetSdk' (Find-Tool 'dotnet.exe') @('--list-sdks')
    $tools += Get-ToolResult 'uv' (Find-Tool 'uv.exe') @('--version')
    $tools += Get-ToolResult 'MKVToolNix' (Find-Tool 'mkvmerge.exe') @('--version')
    $tools += Get-ToolResult 'FFmpeg' (Find-Tool 'ffmpeg.exe') @('-version')
    $tools += Get-ToolResult 'FFprobe' (Find-Tool 'ffprobe.exe') @('-version')
    $inno = Find-Tool 'iscc.exe'
    $tools += Get-ToolResult 'InnoSetup' $inno @('/?')
    $uv = Find-Tool 'uv.exe'
    $python = $null; $mcp = $null
    if ($uv) {
        $candidate = $null
        try {
            $candidate = (& $uv python find $configuration.pythonVersion 2>$null | Out-String).Trim()
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $candidate)) { $python = $candidate }
        } catch { $python = $null }
        $toolBin = (& $uv tool dir --bin | Out-String).Trim()
        $candidate = Join-Path $toolBin 'windows-mcp.exe'
        if (Test-Path -LiteralPath $candidate) { $mcp = $candidate }
    }
    $tools += Get-ToolResult 'Python' $python @('--version')
    $mcpVersion = 'Not installed'
    if ($uv -and $mcp) {
        $toolDirectory = (& $uv tool dir | Out-String).Trim()
        $metadata = Get-ChildItem -Path (Join-Path $toolDirectory 'windows-mcp\Lib\site-packages\windows_mcp-*.dist-info\METADATA') -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($metadata) {
            $versionLine = Get-Content -LiteralPath $metadata.FullName | Where-Object { $_ -match '^Version: ' } | Select-Object -First 1
            if ($versionLine) { $mcpVersion = $versionLine.Substring(9).Trim() }
        }
    }
    $tools += Get-ToolResult 'WindowsMCP' $mcp @() $mcpVersion
    $commit = $null
    if ((Find-Tool 'git.exe') -and (Test-Path -LiteralPath (Join-Path $WorkspaceRoot '.git'))) {
        $commit = (& git -C $WorkspaceRoot rev-parse HEAD 2>$null | Out-String).Trim()
    }
    $mcpTask = Get-ScheduledTask -TaskName 'windows-mcp-server' -ErrorAction SilentlyContinue
    $runnerTask = Get-ScheduledTask -TaskName 'FritzTV-GitHubRunner' -ErrorAction SilentlyContinue
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $McpPort -ErrorAction SilentlyContinue | Select-Object LocalAddress,LocalPort,OwningProcess)
    $report = [ordered]@{
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('o'); ComputerName = $env:COMPUTERNAME
        User = [Security.Principal.WindowsIdentity]::GetCurrent().Name; TimeZone = (Get-TimeZone).Id
        CommitSha = $commit; ToolchainConfiguration = $configuration; Tools = $tools
        AllRequiredToolsInstalled = (@($tools | Where-Object { -not $_.Installed }).Count -eq 0)
        McpTaskState = $(if ($mcpTask) { [string]$mcpTask.State } else { $null })
        RunnerTaskState = $(if ($runnerTask) { [string]$runnerTask.State } else { $null })
        RunnerGuardPresent = (Test-Path -LiteralPath (Join-Path $StateDirectory 'runner-hooks\Assert-TrustedWindowsJob.ps1'))
        RunnerGuardSha256 = $(if (Test-Path -LiteralPath (Join-Path $StateDirectory 'runner-hooks\Assert-TrustedWindowsJob.ps1')) { (Get-FileHash -LiteralPath (Join-Path $StateDirectory 'runner-hooks\Assert-TrustedWindowsJob.ps1') -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null })
        McpListeners = $listeners
        LiveStreamingValidated = $false
        DesktopControlValidated = $false
        Notes = @('Tool installation is distinct from native desktop/live-media acceptance.',
            'Run host MCP handshake and interactive smoke tests separately.',
            'Run opt-in TV/radio RTP/decode tests separately; HTTP/RTSP control alone is insufficient.')
    }
    $path = Join-Path $StateDirectory 'verification.json'
    [IO.File]::WriteAllText($path, ($report | ConvertTo-Json -Depth 10), $utf8)
    Write-Information ('Verification report: {0}' -f $path) -InformationAction Continue
    return $report
}

New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
Update-ProcessPath
if (-not $VerifyOnly) {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run bootstrap from an elevated PowerShell session.' }
    if (-not $SkipPackages) { foreach ($package in $configuration.packages) { Install-WinGetPackage $package } }
    $pwsh = Find-Tool 'pwsh.exe'
    if (-not $pwsh) { throw 'PowerShell 7 must be installed before configuring SSH.' }
    New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' -Force | Out-Null
    New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name 'DefaultShell' -Value $pwsh -PropertyType String -Force | Out-Null
    if ((Get-TimeZone).Id -ne 'W. Europe Standard Time') { Set-TimeZone -Id 'W. Europe Standard Time' }
    if (-not $SkipFirewall) { Set-RtpFirewall }
    if (-not $SkipMcp) { Set-Mcp }
    if ($ConfigureRunner) { Set-InteractiveRunner }
}
Write-VerificationReport
