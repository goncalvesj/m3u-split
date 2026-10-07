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
    [string] $AppName,
    [string] $TenantId = '',
    [string] $SpaClientId = '',
    [string] $ApiScope = '',
    [string] $OnlineApiBaseUrl = '',
    [switch] $EnableSavedProfiles
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'deploy-common.ps1')

$client = Join-Path (Split-Path $PSScriptRoot -Parent) 'client'
$environment = @{
    VITE_ENTRA_TENANT_ID = $TenantId.Trim()
    VITE_ENTRA_SPA_CLIENT_ID = $SpaClientId.Trim()
    VITE_ENTRA_API_SCOPE = $ApiScope.Trim()
    VITE_ONLINE_API_BASE_URL = $OnlineApiBaseUrl.Trim()
}
$provided = @($environment.Values | Where-Object { $_ })
if ($EnableSavedProfiles) {
    if ($provided.Count -ne 4) {
        throw 'Supply all four online values with -EnableSavedProfiles.'
    }
    $tenant = [guid]::Empty
    $spa = [guid]::Empty
    if (-not [guid]::TryParse($environment.VITE_ENTRA_TENANT_ID, [ref] $tenant) -or $tenant -eq [guid]::Empty -or
        -not [guid]::TryParse($environment.VITE_ENTRA_SPA_CLIENT_ID, [ref] $spa) -or $spa -eq [guid]::Empty) {
        throw 'The tenant ID and SPA ID must be valid, nonzero GUIDs.'
    }
    $environment.VITE_ENTRA_TENANT_ID = $tenant.ToString()
    $environment.VITE_ENTRA_SPA_CLIENT_ID = $spa.ToString()
    $scope = [regex]::Match($environment.VITE_ENTRA_API_SCOPE, '^api://([0-9a-fA-F-]{36})/([a-zA-Z][a-zA-Z0-9_.-]{0,127})$')
    $api = [guid]::Empty
    if (-not $scope.Success -or -not [guid]::TryParse($scope.Groups[1].Value, [ref] $api) -or
        $api -eq [guid]::Empty -or $api -eq $spa) {
        throw 'The scope must belong to a separate API registration.'
    }
    $environment.VITE_ENTRA_API_SCOPE = "api://$($api.ToString())/$($scope.Groups[2].Value)"
    $apiUrl = $null
    if (-not [uri]::TryCreate($environment.VITE_ONLINE_API_BASE_URL, [UriKind]::Absolute, [ref] $apiUrl) -or
        $apiUrl.Scheme -ne 'https' -or $apiUrl.IsLoopback -or $apiUrl.UserInfo -or $apiUrl.Query -or $apiUrl.Fragment) {
        throw 'The API base must be an HTTPS URL without local hosts, credentials, query values, or fragments.'
    }
}
$environment.VITE_ONLINE_PROFILES_ENABLED = if ($EnableSavedProfiles) { 'true' } else { 'false' }

$envFiles = @(Get-ChildItem -LiteralPath $client -Force -File |
    Where-Object { $_.Name -like '.env*' -and $_.Name -ne '.env.example' })
if ($envFiles.Count) {
    throw 'The deployment build must not use local .env files. Supply the public online values as script parameters.'
}

$target = Get-ExistingDeploymentApp -SubscriptionId $SubscriptionId -ResourceGroup $ResourceGroup -AppName $AppName -AppType UI
if ($target.Config.linuxFxVersion -notmatch '^NODE\|' -or
    $target.Config.appCommandLine -ne 'pm2 serve /home/site/wwwroot --no-daemon --spa') {
    throw 'The UI app must use Node and the documented PM2 startup command.'
}
if ($EnableSavedProfiles -and $apiUrl.GetLeftPart([UriPartial]::Authority) -eq $target.Url.TrimEnd('/')) {
    throw 'The API and UI must use separate origins.'
}
if (-not $PSCmdlet.ShouldProcess($target.ResourceId, 'Build and deploy the UI package')) {
    return
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('m3u-ui-' + [guid]::NewGuid().ToString('N'))
$dist = Join-Path $work 'dist'
$saved = @{}
foreach ($name in $environment.Keys) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    foreach ($name in $environment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    Push-Location -LiteralPath $client
    try {
        & npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) {
            throw 'The dependency install failed.'
        }
        if ($IsWindows) {
            & $env:ComSpec /d /c "npm run build -- --base / --outDir `"$dist`""
        } else {
            & npm run build -- --base / --outDir $dist
        }
        if ($LASTEXITCODE -ne 0) {
            throw 'The UI build failed.'
        }
    } finally {
        Pop-Location
    }
    foreach ($page in @('index.html', 'auth.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $dist $page) -PathType Leaf)) {
            throw 'The UI build must have both entry pages.'
        }
    }
    Publish-DeploymentDirectory -Directory $dist -ZipPath (Join-Path $work 'ui.zip') `
        -SubscriptionId $SubscriptionId -ResourceGroup $ResourceGroup -AppName $AppName
    Write-Output "UI deployment complete: $($target.Url)"
} finally {
    foreach ($name in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
