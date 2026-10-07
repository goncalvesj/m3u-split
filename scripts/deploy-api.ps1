#Requires -Version 7.0

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [guid] $SubscriptionId,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroup,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $AppName
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'deploy-common.ps1')

$api = Join-Path (Split-Path $PSScriptRoot -Parent) 'api'
$project = Join-Path $api 'api.csproj'
$framework = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.TargetFramework
if ($framework -notmatch '^net(\d+)\.(\d+)$') {
    throw 'The API must have one supported .NET target framework.'
}
$major = [int] $Matches[1]
$runtime = "DOTNET-ISOLATED|$($Matches[1]).$($Matches[2])"
$target = Get-ExistingDeploymentApp -SubscriptionId $SubscriptionId -ResourceGroup $ResourceGroup -AppName $AppName -AppType API
if ($target.Config.linuxFxVersion -ne $runtime -or -not $target.Config.alwaysOn -or
    $target.Settings.WEBSITE_RUN_FROM_PACKAGE -ne '1') {
    throw 'The API app must match the project runtime, use Always On, and use WEBSITE_RUN_FROM_PACKAGE=1.'
}
if (-not $PSCmdlet.ShouldProcess($target.ResourceId, 'Publish and deploy the API package')) {
    return
}

$sdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0) {
    throw 'The .NET SDK check failed.'
}
$sdk = @($sdks | ForEach-Object {
    if ($_ -match '^(\d+\.\d+\.\d+) \[') {
        $version = [version] $Matches[1]
        if ($version.Major -eq $major) { $version }
    }
} | Sort-Object -Descending | Select-Object -First 1)
if (-not $sdk.Count) {
    throw "Install a stable .NET $major SDK before the API deployment."
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('m3u-api-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $work 'publish'
[void] [System.IO.Directory]::CreateDirectory($work)

try {
    @{ sdk = @{ version = $sdk[0].ToString(); rollForward = 'latestPatch' } } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'global.json') -Encoding utf8
    Push-Location -LiteralPath $work
    try {
        & dotnet publish $project --configuration Release --runtime linux-x64 `
            --self-contained false --output $publish --artifacts-path (Join-Path $work 'artifacts')
        if ($LASTEXITCODE -ne 0) {
            throw 'The API publish failed.'
        }
    } finally {
        Pop-Location
    }

    $functions = @(Get-Content -LiteralPath (Join-Path $publish 'functions.metadata') -Raw | ConvertFrom-Json)
    if (-not $functions.Count) {
        throw 'The package has no function metadata.'
    }
    foreach ($function in $functions) {
        if ($function.name -in @('Upload', 'Download')) {
            throw 'The package has a legacy function.'
        }
        foreach ($binding in $function.bindings) {
            if ($binding.type -eq 'httpTrigger' -and $binding.route -notmatch '^(online|feed)/') {
                throw 'The package has an unsupported HTTP route.'
            }
        }
    }
    foreach ($file in @('host.json', 'worker.config.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) {
            throw 'The package root is not complete.'
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publish '.azurefunctions') -PathType Container)) {
        throw 'The package has no worker extension folder.'
    }
    if (Get-ChildItem -LiteralPath $publish -Recurse -Force -File |
        Where-Object { $_.Name -like '.env*' -or $_.Name -like 'local.settings*.json' }) {
        throw 'The package has a local configuration file.'
    }

    Publish-DeploymentDirectory -Directory $publish -ZipPath (Join-Path $work 'api.zip') `
        -SubscriptionId $SubscriptionId -ResourceGroup $ResourceGroup -AppName $AppName
    Write-Output "API deployment complete: $($target.Url)"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
