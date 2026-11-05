[CmdletBinding()]
param([string]$GuardPath = (Join-Path $PSScriptRoot 'Assert-TrustedWindowsJob.ps1'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$guard = (Resolve-Path -LiteralPath $GuardPath).Path
$shell = (Get-Process -Id $PID).Path
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('fritz-runner-guard-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
$payloadPath = Join-Path $temporary 'event.json'
$validPayload = @{ repository = @{ full_name = 'jan-guenter/fritz-tv-app' }; ref = 'main' }
$validEnvironment = @{
    GITHUB_ACTIONS = 'true'
    GITHUB_REPOSITORY = 'jan-guenter/fritz-tv-app'
    GITHUB_REF = 'refs/heads/main'
    GITHUB_EVENT_NAME = 'workflow_dispatch'
    GITHUB_WORKFLOW_REF = 'jan-guenter/fritz-tv-app/.github/workflows/windows-ui.yml@refs/heads/main'
    GITHUB_SHA = ('a' * 40)
    GITHUB_EVENT_PATH = $payloadPath
    RUNNER_ENVIRONMENT = 'self-hosted'
}
$cases = @(
    @{ Name = 'trusted main dispatch'; Changes = @{}; Allowed = $true },
    @{ Name = 'fork pull request'; Changes = @{ GITHUB_EVENT_NAME = 'pull_request' }; Allowed = $false },
    @{ Name = 'pull request target'; Changes = @{ GITHUB_EVENT_NAME = 'pull_request_target' }; Allowed = $false },
    @{ Name = 'untrusted branch'; Changes = @{ GITHUB_REF = 'refs/heads/codex/unreviewed' }; Allowed = $false },
    @{ Name = 'tag dispatch'; Changes = @{ GITHUB_REF = 'refs/tags/v1.0.0' }; Allowed = $false },
    @{ Name = 'another repository'; Changes = @{ GITHUB_REPOSITORY = 'attacker/fork' }; Allowed = $false },
    @{ Name = 'different workflow'; Changes = @{ GITHUB_WORKFLOW_REF = 'jan-guenter/fritz-tv-app/.github/workflows/other.yml@refs/heads/main' }; Allowed = $false },
    @{ Name = 'missing context'; Changes = @{ GITHUB_WORKFLOW_REF = '' }; Allowed = $false },
    @{ Name = 'missing event'; Changes = @{ GITHUB_EVENT_PATH = (Join-Path $temporary 'missing.json') }; Allowed = $false },
    @{ Name = 'invalid SHA'; Changes = @{ GITHUB_SHA = 'not-a-commit' }; Allowed = $false },
    @{ Name = 'hosted runner'; Changes = @{ RUNNER_ENVIRONMENT = 'github-hosted' }; Allowed = $false },
    @{ Name = 'contradictory event'; Changes = @{}; Allowed = $false; Payload = @{ repository = @{ full_name = 'attacker/fork' }; ref = 'main' } },
    @{ Name = 'PR event disguised as dispatch'; Changes = @{}; Allowed = $false; Payload = @{ repository = @{ full_name = 'jan-guenter/fritz-tv-app' }; ref = 'main'; pull_request = @{} } }
)
try {
    foreach ($case in $cases) {
        $payload = if ($case.ContainsKey('Payload')) { $case.Payload } else { $validPayload }
        [IO.File]::WriteAllText($payloadPath, ($payload | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $shell
        $start.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $guard + '"'
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($key in $validEnvironment.Keys) { $start.EnvironmentVariables[$key] = $validEnvironment[$key] }
        foreach ($key in $case.Changes.Keys) { $start.EnvironmentVariables[$key] = $case.Changes[$key] }
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $start
        try {
            $process.Start() | Out-Null
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(10000)) { $process.Kill(); throw ('Guard timed out: ' + $case.Name) }
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            $allowed = $process.ExitCode -eq 0
            if ($allowed -ne $case.Allowed) { throw ('Unexpected guard result for ' + $case.Name + ': ' + $stdout + $stderr) }
            Write-Output ('PASS: ' + $case.Name)
        } finally {
            $process.Dispose()
        }
    }
    Write-Output ('Runner guard: ' + $cases.Count + ' policy cases passed.')
} finally {
    if ([IO.Directory]::Exists($temporary)) { [IO.Directory]::Delete($temporary, $true) }
}
