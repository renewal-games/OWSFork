#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Claim', 'Update', 'Complete')]
    [string]$Action = 'Status',
    [string]$Repository = (Get-Location).Path,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]{0,119}$')]
    [string]$SessionId,
    [string]$Owner,
    [string]$Task,
    [string[]]$Scope,
    [ValidateSet('active', 'blocked', 'paused')]
    [string]$State = 'active',
    [string]$Note,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

function Invoke-RepoGit {
    param([string]$At, [string[]]$GitArgs)
    # Trust only the explicitly selected checkout for this invocation. No global exception.
    $output = @(& git -c "safe.directory=$($At.Replace('\', '/'))" -C $At @GitArgs 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($GitArgs -join ' ') failed in ${At}: $($output -join "`n")"
    }
    return $output
}

$Repository = (Resolve-Path -LiteralPath $Repository).Path
$root = [string](Invoke-RepoGit $Repository @('rev-parse', '--show-toplevel'))
$common = [string](Invoke-RepoGit $Repository @('rev-parse', '--path-format=absolute', '--git-common-dir'))
if ((Split-Path $common -Leaf) -ne '.git') {
    throw 'This workflow requires a normal primary checkout with a .git directory.'
}
$shared = Join-Path (Split-Path $common -Parent) '.agent-state/sessions'

function Read-Sessions {
    if (Test-Path -LiteralPath $shared) {
        Get-ChildItem -LiteralPath $shared -Filter '*.json' -File | ForEach-Object {
            Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        }
    }
}

if ($Action -eq 'Status') {
    $sessions = @(Read-Sessions)
    $paths = @(Invoke-RepoGit $root @('worktree', 'list', '--porcelain') |
        Where-Object { $_ -like 'worktree *' } | ForEach-Object { $_.Substring(9) })
    $worktrees = @(foreach ($path in $paths) {
        try {
            $changes = @(Invoke-RepoGit $path @('status', '--short', '--untracked-files=normal'))
            $counts = ([string](Invoke-RepoGit $path @('rev-list', '--left-right', '--count', 'main...HEAD'))).Split("`t")
            $patches = @(Invoke-RepoGit $path @('cherry', 'main', 'HEAD'))
            $owners = @($sessions | Where-Object { $_.worktree -eq $path -and $_.state -ne 'complete' })
            [pscustomobject]@{
                path = $path
                branch = [string](Invoke-RepoGit $path @('branch', '--show-current'))
                head = [string](Invoke-RepoGit $path @('rev-parse', 'HEAD'))
                changedEntries = $changes.Count
                changes = $changes
                behindMain = [int]$counts[0]
                aheadMain = [int]$counts[1]
                uniquePatches = @($patches | Where-Object { $_ -like '+ *' }).Count
                equivalentPatches = @($patches | Where-Object { $_ -like '- *' }).Count
                rootGuidance = Test-Path -LiteralPath (Join-Path $path 'AGENTS.md')
                sessions = @($owners | ForEach-Object { $_.sessionId })
                activity = $(if ($owners.Count) { 'Recorded; verify last update' } else { 'Unknown' })
                error = $null
            }
        } catch {
            [pscustomobject]@{ path = $path; error = $_.Exception.Message }
        }
    })
    $result = [pscustomobject]@{
        observedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        comparison = 'Local main; no fetch performed. Patch equivalence is not cleanup authorization.'
        register = $shared
        worktrees = $worktrees
        sessions = $sessions
    }
    if ($Json) { $result | ConvertTo-Json -Depth 10 }
    else {
        $worktrees | Format-Table path, changedEntries, behindMain, aheadMain, uniquePatches, activity, error -AutoSize
        $sessions | Format-Table sessionId, owner, state, updatedUtc, task, note -Wrap
        Write-Output "Register: $shared"
        Write-Output $result.comparison
    }
    if (@($worktrees | Where-Object { $_.error }).Count) { exit 1 }
    exit 0
}

if (-not $SessionId) { throw '-SessionId is required for Claim, Update, and Complete.' }
$file = Join-Path $shared "$SessionId.json"
$now = [DateTimeOffset]::UtcNow.ToString('o')
if ($Action -eq 'Claim') {
    if (-not $Owner -or -not $Task -or -not $Scope) { throw 'Claim requires -Owner, -Task, and -Scope.' }
    $existing = @(Read-Sessions | Where-Object { $_.state -ne 'complete' -and $_.worktree -eq $root })
    if ($existing.Count) {
        Write-Warning "Other sessions claim this checkout: $($existing.sessionId -join ', '). Compare scopes before editing."
    }
    $record = [pscustomobject]@{
        sessionId = $SessionId; owner = $Owner; task = $Task; scope = @($Scope)
        worktree = $root; branch = [string](Invoke-RepoGit $root @('branch', '--show-current'))
        state = $State; startedUtc = $now; updatedUtc = $now; note = $Note
    }
    New-Item -ItemType Directory -Path $shared -Force | Out-Null
    # CreateNew refuses to overwrite another session's existing record.
    $stream = [IO.File]::Open($file, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 5))
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
} else {
    $record = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ($record.worktree -ne $root) { throw 'Update the session from its registered worktree.' }
    if ($record.state -eq 'complete') { throw 'This session is complete; claim a new session ID for new work.' }
    if ($Action -eq 'Complete' -and -not $Note) { throw 'Complete requires a handoff -Note, including validation and remaining work.' }
    if ($PSBoundParameters.ContainsKey('Task')) { $record.task = $Task }
    if ($PSBoundParameters.ContainsKey('Scope')) { $record.scope = @($Scope) }
    if ($PSBoundParameters.ContainsKey('Note')) { $record.note = $Note }
    $record.branch = [string](Invoke-RepoGit $root @('branch', '--show-current'))
    $record.state = $(if ($Action -eq 'Complete') { 'complete' } else { $State })
    $record.updatedUtc = $now
    $temp = "$file.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temp, ($record | ConvertTo-Json -Depth 5))
        [IO.File]::Move($temp, $file, $true)
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
    }
}
$record | ConvertTo-Json -Depth 5
