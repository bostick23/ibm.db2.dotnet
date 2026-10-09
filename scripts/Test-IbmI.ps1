[CmdletBinding()]
param(
    [string] $Filter = 'Category=Integration',
    [string] $ResultsDirectory = 'TestResults/ibmi',
    [string] $EnvFile = 'tests/Db2i.Tests/.env.local',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot

function Resolve-RepositoryPath([string] $Path) {
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $repositoryPath $Path))
}

function Read-TestEnvironment([string] $Path) {
    $variables = @{}
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $Path) {
        $lineNumber++
        if ($line -cnotmatch '^\s*(?:export\s+)?(DB2I_TEST_[A-Za-z0-9_]+)\s*=(.*)$') {
            continue
        }

        $name = $Matches[1]
        $value = $Matches[2].Trim()
        if ($value.StartsWith('"') -or $value.StartsWith("'")) {
            $quote = $value[0]
            $closingQuote = -1
            for ($index = 1; $index -lt $value.Length; $index++) {
                if ($value[$index] -eq $quote -and $value.Substring($index + 1) -match '^\s*(?:#.*)?$') {
                    $closingQuote = $index
                    break
                }
            }
            if ($closingQuote -lt 1) {
                throw "Invalid quoted DB2I_TEST value on line $lineNumber of the environment file."
            }

            # Values are literal: do not expand variables or execute PowerShell expressions.
            $value = $value.Substring(1, $closingQuote - 1)
        }
        else {
            $value = ($value -replace '\s+#.*$', '').TrimEnd()
        }

        $variables[$name] = $value
    }

    return $variables
}

$environmentPath = Resolve-RepositoryPath $EnvFile
if (-not (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
    throw "Environment file not found: $environmentPath. Create the local file or supply -EnvFile."
}

# Parse everything before changing the process environment, including on invalid input.
$testVariables = Read-TestEnvironment $environmentPath
$previousVariables = @{}
$testExitCode = 1
$locationChanged = $false
try {
    foreach ($name in $testVariables.Keys) {
        $previousVariables[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $testVariables[$name], 'Process')
    }

    $resultsPath = Resolve-RepositoryPath $ResultsDirectory
    $null = New-Item -ItemType Directory -Path $resultsPath -Force
    $trxName = 'ibmi-{0}-{1}.trx' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'), $PID
    $testArguments = @(
        'test', (Join-Path $repositoryPath 'Db2i.sln'),
        '--configuration', 'Release',
        '--filter', $Filter,
        '--results-directory', $resultsPath,
        '--logger', 'console;verbosity=normal',
        '--logger', "trx;LogFileName=$trxName"
    )
    if ($NoBuild) {
        $testArguments += '--no-build'
    }

    # Keep native failure handling explicit so finally always restores the environment.
    $PSNativeCommandUseErrorActionPreference = $false
    Push-Location -LiteralPath $repositoryPath
    $locationChanged = $true
    & dotnet @testArguments
    $testExitCode = $LASTEXITCODE
}
finally {
    if ($locationChanged) {
        Pop-Location
    }
    foreach ($name in $previousVariables.Keys) {
        # PowerShell converts $null to an empty string for string parameters.
        # Preserve the difference between a missing variable and an empty value.
        $previousValue = $previousVariables[$name]
        if ($null -eq $previousValue) {
            $previousValue = [NullString]::Value
        }
        [Environment]::SetEnvironmentVariable($name, $previousValue, 'Process')
    }
}

exit $testExitCode
