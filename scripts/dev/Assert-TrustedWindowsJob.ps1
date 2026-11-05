# Runner-local pre-job gate. Install a verified copy OUTSIDE the checkout and set
# ACTIONS_RUNNER_HOOK_JOB_STARTED in the runner process environment or .env file.
# No trust policy is accepted through workflow-controlled inputs or parameters.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Stop-UntrustedJob {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'This fail-closed trust guard only reports an error and exits; WhatIf must never bypass rejection.')]
    param([string]$Reason)
    [Console]::Error.WriteLine('FRITZ TV runner denied job: ' + $Reason)
    exit 1
}

$expectedRepository = 'jan-guenter/fritz-tv-app'
$expectedRef = 'refs/heads/main'
$expectedWorkflow = 'jan-guenter/fritz-tv-app/.github/workflows/windows-ui.yml@refs/heads/main'
$checks = @{
    GITHUB_ACTIONS = 'true'
    GITHUB_REPOSITORY = $expectedRepository
    GITHUB_REF = $expectedRef
    GITHUB_EVENT_NAME = 'workflow_dispatch'
    GITHUB_WORKFLOW_REF = $expectedWorkflow
    RUNNER_ENVIRONMENT = 'self-hosted'
}
foreach ($name in $checks.Keys) {
    $actual = [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::Process)
    if (-not [string]::Equals($actual, $checks[$name], [StringComparison]::Ordinal)) {
        Stop-UntrustedJob ('unexpected or missing ' + $name)
    }
}

$sha = [Environment]::GetEnvironmentVariable('GITHUB_SHA', [EnvironmentVariableTarget]::Process)
if (-not $sha -or $sha -notmatch '^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$') {
    Stop-UntrustedJob 'missing or malformed trusted commit SHA'
}

try {
    $eventPath = [Environment]::GetEnvironmentVariable('GITHUB_EVENT_PATH', [EnvironmentVariableTarget]::Process)
    if (-not $eventPath -or -not [IO.Path]::IsPathRooted($eventPath) -or -not [IO.File]::Exists($eventPath)) {
        Stop-UntrustedJob 'missing runner event payload'
    }
    if (([IO.FileInfo]$eventPath).Length -gt 1048576) {
        Stop-UntrustedJob 'event payload exceeds expected dispatch size'
    }
    $payload = [IO.File]::ReadAllText($eventPath) | ConvertFrom-Json
    if (-not [string]::Equals([string]$payload.repository.full_name, $expectedRepository, [StringComparison]::Ordinal)) {
        Stop-UntrustedJob 'event repository does not match trusted context'
    }
    if (-not [string]::Equals([string]$payload.ref, 'main', [StringComparison]::Ordinal) -and
        -not [string]::Equals([string]$payload.ref, $expectedRef, [StringComparison]::Ordinal)) {
        Stop-UntrustedJob 'event ref does not match protected main'
    }
    if ($null -ne $payload.PSObject.Properties['pull_request']) {
        Stop-UntrustedJob 'pull request event payload is never permitted'
    }
} catch {
    Stop-UntrustedJob 'invalid or incomplete runner event payload'
}

Write-Output 'Trusted FRITZ TV Windows desktop workflow accepted.'
exit 0
