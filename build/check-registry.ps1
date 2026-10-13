param(
    [Parameter(Mandatory)] [string] $Cli,
    [Parameter(Mandatory)] [string] $Work,
    [Parameter(Mandatory)] [string] $Checks,
    [string] $Branches = '',
    [switch] $All,
    [string] $Repository = 'https://github.com/BasisVR/Basis.git'
)

$ErrorActionPreference = 'Stop'
$basispm = Join-Path $Cli $(if ($IsWindows) { 'basispm.exe' } else { 'basispm' })
if (-not (Test-Path $basispm)) { throw "The CLI wasn't found at $basispm." }
$checksPath = [System.IO.Path]::GetFullPath($Checks)

$heads = @{}
foreach ($line in git ls-remote --heads $Repository) {
    $sha, $ref = $line -split "`t"
    $heads[$ref -replace '^refs/heads/', ''] = $sha
}
if ($heads.Count -eq 0) { throw "Couldn't list the branches of $Repository." }

if ($Branches.Trim()) {
    $wanted = $Branches -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
} else {
    $lts = $heads.Keys | Where-Object { $_ -like 'long-term-support*' } | Sort-Object -Descending | Select-Object -First 1
    $wanted = @('developer', $lts, 'experimental-branch-major-changes') | Where-Object { $_ -and $heads.ContainsKey($_) }
}

$recorded = $null
if (Test-Path $checksPath) { $recorded = (Get-Content $checksPath -Raw | ConvertFrom-Json).basis }

$failed = 0
foreach ($branch in $wanted) {
    if (-not $heads.ContainsKey($branch)) { Write-Warning "Basis has no branch called $branch."; $failed++; continue }
    $head = $heads[$branch]
    $last = if ($recorded) { $recorded.$branch.commit } else { $null }
    if (-not $All -and $last -eq $head) { Write-Host "Basis $branch hasn't moved since the last check ($($head.Substring(0, 9)))."; continue }

    $clone = Join-Path $Work $branch
    if (Test-Path (Join-Path $clone '.git')) {
        git -C $clone fetch --depth 1 origin $branch
        if ($LASTEXITCODE -eq 0) { git -C $clone reset --hard FETCH_HEAD }
        if ($LASTEXITCODE -eq 0) { git -C $clone clean -ffdx -e '/Basis/Library/' }
    } else {
        git clone --depth 1 --branch $branch $Repository $clone
    }
    if ($LASTEXITCODE -ne 0) { Write-Warning "Couldn't get Basis $branch."; $failed++; continue }

    Write-Host "Checking the registry packages on Basis $branch ($($head.Substring(0, 9)))"
    & $basispm --project (Join-Path $clone 'Basis') check-registry --record $checksPath --yes
    if ($LASTEXITCODE -ne 0) { $failed++ }
}

if ($failed -gt 0) { Write-Warning "$failed branch check(s) didn't finish." }
exit 0
