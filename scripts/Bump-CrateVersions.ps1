[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$cratesRoot = Join-Path $repoRoot 'crates'
$readmePath = Join-Path $repoRoot 'README.md'
$sdkProjectPath = Join-Path $repoRoot 'dotnet\sdk-ffi\Devolutions.MultiPwsh.Sdk.csproj'
$packageExamplePaths = @(
    $readmePath,
    (Join-Path $repoRoot 'docs\host-and-venv.md'),
    (Join-Path $repoRoot 'nuget\Devolutions.MultiPwsh.Cli\README.md')
)

if (-not (Test-Path -Path $cratesRoot -PathType Container)) {
    throw "Crates directory not found: $cratesRoot"
}

$cargoFiles = Get-ChildItem -Path $cratesRoot -Directory |
    ForEach-Object { Join-Path $_.FullName 'Cargo.toml' } |
    Where-Object { Test-Path -Path $_ -PathType Leaf }

if (-not $cargoFiles) {
    throw "No crate Cargo.toml files found under $cratesRoot"
}

$encoding = New-Object System.Text.UTF8Encoding($false)
$updated = @()
$readmeUpdated = $false
$sdkProjectUpdated = $false
$packageExamplesUpdated = @()

foreach ($cargoFile in $cargoFiles) {
    $content = [System.IO.File]::ReadAllText($cargoFile)

    $pattern = '(?ms)^(?<prefix>\[package\]\s*.*?^version\s*=\s*")(?<current>[^"\r\n]+)(?<suffix>")'
    $regex = [System.Text.RegularExpressions.Regex]::new(
        $pattern,
        [System.Text.RegularExpressions.RegexOptions]::Multiline -bor [System.Text.RegularExpressions.RegexOptions]::Singleline,
        [System.TimeSpan]::FromSeconds(5)
    )
    $match = $regex.Match($content)
    if (-not $match.Success) {
        throw "Could not find package version field in $cargoFile"
    }

    $currentVersion = $match.Groups['current'].Value
    if ($currentVersion -eq $Version) {
        continue
    }

    $newContent = $regex.Replace(
        $content,
        [System.Text.RegularExpressions.MatchEvaluator] {
            param($cargoMatch)
            $cargoMatch.Groups['prefix'].Value + $Version + $cargoMatch.Groups['suffix'].Value
        },
        1
    )

    if (-not $DryRun) {
        [System.IO.File]::WriteAllText($cargoFile, $newContent, $encoding)
    }

    $updated += $cargoFile
}

if ($updated.Count -gt 0 -and -not $DryRun) {
    Push-Location -Path $repoRoot
    try {
        cargo update -w
    }
    finally {
        Pop-Location
    }
}

if (Test-Path -Path $readmePath -PathType Leaf) {
    $readmeContent = [System.IO.File]::ReadAllText($readmePath)
    $newReadmeContent = $readmeContent
    $readmePatterns = @(
        '(?m)(?<prefix>Install a specific tag \(example `v)(?<current>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)(?<suffix>`\):)',
        '(?m)(?<prefix>releases/download/v)(?<current>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)(?<suffix>/install-multi-pwsh\.(?:sh|ps1))',
        '(?m)(?<prefix>bash -s -- v)(?<current>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)',
        '(?m)(?<prefix>-Version v)(?<current>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)'
    )

    foreach ($pattern in $readmePatterns) {
        $newReadmeContent = [System.Text.RegularExpressions.Regex]::Replace(
            $newReadmeContent,
            $pattern,
            [System.Text.RegularExpressions.MatchEvaluator] {
                param($readmeMatch)
                $readmeMatch.Groups['prefix'].Value + $Version + $readmeMatch.Groups['suffix'].Value
            },
            [System.Text.RegularExpressions.RegexOptions]::Multiline,
            [System.TimeSpan]::FromSeconds(5)
        )
    }

    if ($newReadmeContent -ne $readmeContent) {
        if (-not $DryRun) {
            [System.IO.File]::WriteAllText($readmePath, $newReadmeContent, $encoding)
        }

        $readmeUpdated = $true
    }
}

if (Test-Path -Path $sdkProjectPath -PathType Leaf) {
    $sdkProjectContent = [System.IO.File]::ReadAllText($sdkProjectPath)
    $newSdkProjectContent = [System.Text.RegularExpressions.Regex]::Replace(
        $sdkProjectContent,
        '(?<prefix><PackageVersion\b[^>]*>)(?<current>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)(?<suffix></PackageVersion>)',
        [System.Text.RegularExpressions.MatchEvaluator] {
            param($sdkProjectMatch)
            $sdkProjectMatch.Groups['prefix'].Value + $Version + $sdkProjectMatch.Groups['suffix'].Value
        },
        [System.Text.RegularExpressions.RegexOptions]::None,
        [System.TimeSpan]::FromSeconds(5)
    )

    if ($newSdkProjectContent -ne $sdkProjectContent) {
        if (-not $DryRun) {
            [System.IO.File]::WriteAllText($sdkProjectPath, $newSdkProjectContent, $encoding)
        }

        $sdkProjectUpdated = $true
    }
}

foreach ($packageExamplePath in $packageExamplePaths) {
    if (-not (Test-Path -Path $packageExamplePath -PathType Leaf)) {
        continue
    }

    $content = [System.IO.File]::ReadAllText($packageExamplePath)
    $newContent = [System.Text.RegularExpressions.Regex]::Replace(
        $content,
        '(?<prefix>PackageReference Include="Devolutions\.MultiPwsh\.Cli" Version=")(?<current>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)(?<suffix>")',
        [System.Text.RegularExpressions.MatchEvaluator] {
            param($packageMatch)
            $packageMatch.Groups['prefix'].Value + $Version + $packageMatch.Groups['suffix'].Value
        },
        [System.Text.RegularExpressions.RegexOptions]::None,
        [System.TimeSpan]::FromSeconds(5)
    )

    if ($newContent -ne $content) {
        if (-not $DryRun) {
            [System.IO.File]::WriteAllText($packageExamplePath, $newContent, $encoding)
        }

        $packageExamplesUpdated += $packageExamplePath
    }
}

if ($updated.Count -eq 0 -and -not $readmeUpdated -and -not $sdkProjectUpdated -and $packageExamplesUpdated.Count -eq 0) {
    Write-Host "All crate package versions are already $Version"
    if (Test-Path -Path $readmePath -PathType Leaf) {
        Write-Host "No README release example tag needed updating"
    }
    exit 0
}

if ($updated.Count -gt 0) {
    Write-Host "Updated crate versions to ${Version}:"
    $updated | ForEach-Object { Write-Host " - $_" }
    if (-not $DryRun) {
        Write-Host "Refreshed Cargo.lock"
    }
}

if ($readmeUpdated) {
    Write-Host "Updated README release example tag in: $readmePath"
}

if ($sdkProjectUpdated) {
    Write-Host "Updated SDK package version in: $sdkProjectPath"
}

if ($packageExamplesUpdated.Count -gt 0) {
    Write-Host "Updated NuGet package reference versions in:"
    $packageExamplesUpdated | ForEach-Object { Write-Host " - $_" }
}
