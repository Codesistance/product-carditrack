<#
.SYNOPSIS
    Mints a dev push token and fires one test push, so a notification can be observed on a device
    or emulator on demand.

.DESCRIPTION
    Computes the HMAC-SHA256 token that POST /api/v1/dev/push requires (notification_engine.md
    §13, "Firing a push by hand") and, unless -TokenOnly is given, sends the request and prints
    the result.

    The token is computed exactly as DevPushTokenService does:

        payload = "devpush|{userId:N}|{category}|{severity or -}|{expiresAtUnixSeconds}"
        token   = "{expiresAtUnixSeconds}.{base64url(HMAC-SHA256(key, payload))}"

    Because the target and payload shape are inside the MAC, a token is good for exactly one
    (user, category, severity) combination and nothing else.

    The endpoint only exists when Dev:PushTokenKey is configured on the API and the environment is
    not prod. If it 404s, the API did not start with the key set.

.PARAMETER UserId
    The recipient user's id. This is the caregiver who receives the push, not the wearer.

.PARAMETER Category
    Safety (default), Health, or Nudge. Only Safety and Health-at-Red/Orange actually push —
    a Nudge request returns 200 with an explanatory hint and no notification.

.PARAMETER Severity
    Red, Orange, Yellow, or Green. Required for Health. Ignored otherwise.

.PARAMETER ApiBaseUrl
    Defaults to http://localhost:5230, the API's launchSettings http profile. An emulator reaching
    the host uses http://10.0.2.2:5230.

.PARAMETER Key
    Base64-encoded 256-bit key. Defaults to $env:Dev__PushTokenKey.

.PARAMETER TtlSeconds
    Token lifetime, 1-600 (the service rejects anything longer). Default 120.

.PARAMETER TokenOnly
    Print the token and exit without sending.

.PARAMETER SkipCertificateCheck
    Accept the ASP.NET Core development certificate.

.EXAMPLE
    # Generate a key once, then keep it on the API side:
    #   $key = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Max 256 }))
    #   dotnet user-secrets set "Dev:PushTokenKey" $key --project src/Presentation/CardiTrack.API
    #   $env:Dev__PushTokenKey = $key

    ./scripts/dev-push.ps1 -UserId 8f14e45f-ea8d-4b09-9b3c-1d2e3f4a5b6c

.EXAMPLE
    ./scripts/dev-push.ps1 -UserId 8f14e45f-ea8d-4b09-9b3c-1d2e3f4a5b6c -Category Health -Severity Red
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [guid] $UserId,

    [ValidateSet('Safety', 'Health', 'Nudge')]
    [string] $Category = 'Safety',

    [ValidateSet('Red', 'Orange', 'Yellow', 'Green')]
    [string] $Severity,

    [string] $ApiBaseUrl = 'http://localhost:5230',

    [string] $Key = $env:Dev__PushTokenKey,

    [ValidateRange(1, 600)]
    [int] $TtlSeconds = 120,

    [switch] $TokenOnly,

    [switch] $SkipCertificateCheck
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Key)) {
    throw "No key. Pass -Key, or set `$env:Dev__PushTokenKey to the same base64 256-bit value the API has in Dev:PushTokenKey."
}

$keyBytes = [Convert]::FromBase64String($Key)
if ($keyBytes.Length -ne 32) {
    throw "Dev:PushTokenKey must decode to 32 bytes (256 bits); this one decodes to $($keyBytes.Length)."
}

if ($Category -eq 'Health' -and -not $Severity) {
    throw "Health needs -Severity. Only Red and Orange produce a push; Yellow and Green are in-app only."
}

# Must match DevPushTokenService.Compute exactly: "N" format for the guid (no dashes), the enum
# name as C# renders it, and "-" standing in for an absent severity so the field count is fixed.
$expiresAt = [DateTimeOffset]::UtcNow.AddSeconds($TtlSeconds).ToUnixTimeSeconds()
$severityPart = if ($Severity) { $Severity } else { '-' }
$payload = "devpush|$($UserId.ToString('N'))|$Category|$severityPart|$expiresAt"

$hmac = [System.Security.Cryptography.HMACSHA256]::new($keyBytes)
try {
    $mac = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))
}
finally {
    $hmac.Dispose()
}

$base64Url = [Convert]::ToBase64String($mac).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$token = "$expiresAt.$base64Url"

if ($TokenOnly) {
    Write-Output $token
    return
}

$body = @{ userId = $UserId.ToString(); category = $Category }
if ($Severity) { $body.severity = $Severity }

$request = @{
    Uri         = "$($ApiBaseUrl.TrimEnd('/'))/api/v1/dev/push"
    Method      = 'Post'
    ContentType = 'application/json'
    Headers     = @{ 'X-Dev-Push-Token' = $token }
    Body        = ($body | ConvertTo-Json -Compress)
}
if ($SkipCertificateCheck) {
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        $request.SkipCertificateCheck = $true
    }
    else {
        # Windows PowerShell 5.1 has no -SkipCertificateCheck; the callback is the only lever, and
        # it is process-wide, so this script must stay short-lived.
        [Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
    }
}

Write-Host "POST $($request.Uri)  ($Category$(if ($Severity) { "/$Severity" }) -> $UserId)" -ForegroundColor Cyan

$response = Invoke-RestMethod @request
$response.data | ConvertTo-Json -Depth 5

if ($response.data.hint) {
    # The 200-but-nothing-arrived cases. Louder than the JSON above, because this is the answer
    # whenever the emulator stays quiet.
    Write-Host ''
    Write-Warning $response.data.hint
}
