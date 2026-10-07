#Requires -Version 7.0

function Invoke-DeployAzureCli {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $output = & az @Arguments --output json --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw 'The Azure CLI command failed. Check its error before another attempt.'
    }
    if (-not $output) {
        throw 'The Azure CLI sent no result.'
    }
    $output | ConvertFrom-Json
}

function Get-ExistingDeploymentApp {
    param(
        [Parameter(Mandatory)]
        [guid] $SubscriptionId,
        [Parameter(Mandatory)]
        [string] $ResourceGroup,
        [Parameter(Mandatory)]
        [string] $AppName,
        [Parameter(Mandatory)]
        [ValidateSet('UI', 'API')]
        [string] $AppType
    )

    $account = Invoke-DeployAzureCli @('account', 'show', '--subscription', $SubscriptionId.ToString())
    if ($account.id -ne $SubscriptionId.ToString()) {
        throw 'The Azure account does not match the selected subscription.'
    }

    $group = [uri]::EscapeDataString($ResourceGroup)
    $name = [uri]::EscapeDataString($AppName)
    $resourceId = "/subscriptions/$SubscriptionId/resourceGroups/$group/providers/Microsoft.Web/sites/$name"
    $url = "https://management.azure.com$resourceId"
    $app = Invoke-DeployAzureCli @('rest', '--subscription', $SubscriptionId.ToString(),
        '--method', 'get', '--url', "$url`?api-version=2025-03-01")
    $kinds = @($app.kind -split ',')
    if ('linux' -notin $kinds) {
        throw 'These scripts support Linux App Service apps only.'
    }
    if (($AppType -eq 'API') -ne ('functionapp' -in $kinds)) {
        throw 'The selected app does not match the package type.'
    }
    if (-not $app.properties.httpsOnly) {
        throw 'The selected app must use HTTPS only.'
    }

    $config = Invoke-DeployAzureCli @('rest', '--subscription', $SubscriptionId.ToString(),
        '--method', 'get', '--url', "$url/config/web?api-version=2025-03-01")
    $policy = Invoke-DeployAzureCli @('rest', '--subscription', $SubscriptionId.ToString(),
        '--method', 'get', '--url', "$url/basicPublishingCredentialsPolicies/scm?api-version=2025-03-01")
    if ($policy.properties.allow -ne $false) {
        throw 'Disable SCM basic authentication before deployment. The scripts use Microsoft Entra authentication.'
    }
    $settings = Invoke-DeployAzureCli @('rest', '--subscription', $SubscriptionId.ToString(),
        '--method', 'post', '--url', "$url/config/appsettings/list?api-version=2025-03-01")
    if ($settings.properties.SCM_DO_BUILD_DURING_DEPLOYMENT -ne 'false') {
        throw 'Set SCM_DO_BUILD_DURING_DEPLOYMENT to false before deployment.'
    }

    [pscustomobject]@{
        ResourceId = $app.id
        Url = "https://$($app.properties.defaultHostName)/"
        Config = $config.properties
        Settings = $settings.properties
    }
}

function Publish-DeploymentDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Directory,
        [Parameter(Mandatory)]
        [string] $ZipPath,
        [Parameter(Mandatory)]
        [guid] $SubscriptionId,
        [Parameter(Mandatory)]
        [string] $ResourceGroup,
        [Parameter(Mandatory)]
        [string] $AppName
    )

    [System.IO.Compression.ZipFile]::CreateFromDirectory($Directory, $ZipPath)
    $result = Invoke-DeployAzureCli @(
        'webapp', 'deploy',
        '--subscription', $SubscriptionId.ToString(),
        '--resource-group', $ResourceGroup,
        '--name', $AppName,
        '--src-path', $ZipPath,
        '--type', 'zip',
        '--restart', 'true',
        '--timeout', '600000'
    )
    if ($result.status -ne 4 -or -not $result.complete) {
        throw 'Azure did not confirm a complete deployment. Examine the deployment before another attempt.'
    }
}
