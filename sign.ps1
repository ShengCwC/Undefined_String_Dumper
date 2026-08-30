[CmdletBinding(DefaultParameterSetName = "Store")]
param(
    [string]$ExecutablePath = "artifacts\win-x64\UndefinedStringDumper.exe",

    [Parameter(Mandatory = $true, ParameterSetName = "Pfx")]
    [string]$PfxPath,

    [Parameter(Mandatory = $true, ParameterSetName = "Pfx")]
    [string]$PfxPassword,

    [Parameter(Mandatory = $true, ParameterSetName = "Store")]
    [string]$CertificateThumbprint,

    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$resolvedExecutable = if ([System.IO.Path]::IsPathRooted($ExecutablePath)) {
    [System.IO.Path]::GetFullPath($ExecutablePath)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $ExecutablePath))
}
if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw "Executable not found: $resolvedExecutable"
}

if ($PSCmdlet.ParameterSetName -eq "Pfx") {
    $resolvedPfx = if ([System.IO.Path]::IsPathRooted($PfxPath)) {
        [System.IO.Path]::GetFullPath($PfxPath)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $PfxPath))
    }
    if (-not (Test-Path -LiteralPath $resolvedPfx -PathType Leaf)) {
        throw "PFX file not found: $resolvedPfx"
    }

    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
    $certificate.Import(
        $resolvedPfx,
        $PfxPassword,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet)
}
else {
    $certificate = Get-ChildItem "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
}

if (-not $certificate.HasPrivateKey) {
    throw "The selected certificate does not contain a private key."
}
if ($certificate.NotAfter -le [DateTime]::Now) {
    throw "The selected certificate has expired."
}

$null = Set-AuthenticodeSignature `
    -FilePath $resolvedExecutable `
    -Certificate $certificate `
    -HashAlgorithm SHA256 `
    -TimestampServer $TimestampServer

$verifiedSignature = Get-AuthenticodeSignature -LiteralPath $resolvedExecutable
if ($null -eq $verifiedSignature.SignerCertificate -or
    $verifiedSignature.SignatureType -ne "Authenticode") {
    throw "Signing failed: the executable does not contain an Authenticode signature."
}
if ($verifiedSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
    throw "Signing failed: the embedded signer does not match the selected certificate."
}
if (-not [string]::IsNullOrWhiteSpace($TimestampServer) -and
    $null -eq $verifiedSignature.TimeStamperCertificate) {
    throw "Signing failed: the executable does not contain a timestamp signature."
}
if ($verifiedSignature.Status -notin @("Valid", "UnknownError")) {
    throw "Signing failed: $($verifiedSignature.Status) - $($verifiedSignature.StatusMessage)"
}

$hash = (Get-FileHash -LiteralPath $resolvedExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
$executableName = [System.IO.Path]::GetFileName($resolvedExecutable)
"$hash  $executableName" |
    Set-Content -LiteralPath "$resolvedExecutable.sha256" -Encoding ascii

Write-Host "Signed: $resolvedExecutable"
Write-Host "Signer: $($verifiedSignature.SignerCertificate.Subject)"
Write-Host "Signer thumbprint: $($verifiedSignature.SignerCertificate.Thumbprint)"
Write-Host "Timestamp signer: $($verifiedSignature.TimeStamperCertificate.Subject)"
Write-Host "Status: $($verifiedSignature.Status)"
