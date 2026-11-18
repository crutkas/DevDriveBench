#Requires -Version 5.1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$bundlePath = Join-Path $PSScriptRoot 'DevDriveBench-1.0.0.msixbundle'
$certificatePath = Join-Path $PSScriptRoot 'DevDriveBench.cer'
$bundleHash = '45F341CE4B27171E40BA12A49156405C6AA5AE88167DF44FF31D3C8DA18FDE1B'
$certificateHash = '22BC2440634EBD37DD1E9DD283642EF07B3B5DC8E278BC6E03B66261AE48C69A'
$thumbprint = 'FF88E4914994F483A7B163DCA33DD32B1ACC9975'

foreach ($file in @($bundlePath, $certificatePath)) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Download DevDriveBench-1.0.0.msixbundle and DevDriveBench.cer from the v1.0.0 release into the same folder as install.ps1. Missing: $file"
    }
}

if ((Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash -ne $bundleHash) {
    throw 'The installer checksum does not match v1.0.0. Download it again from the release.'
}
if ((Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash -ne $certificateHash) {
    throw 'The certificate checksum does not match v1.0.0. Download it again from the release.'
}

if (!(Test-Path -LiteralPath "Cert:\LocalMachine\TrustedPeople\$thumbprint")) {
    Write-Host 'This release is self-signed. Approve the UAC prompt only if you trust Dev Drive Bench.'
    $escapedPath = $certificatePath.Replace("'", "''")
    # Elevate certificate trust only; install below in the original user's session.
    $trustCommand = @"
`$ErrorActionPreference = 'Stop'
try {
    if ((Get-FileHash -LiteralPath '$escapedPath' -Algorithm SHA256).Hash -ne '$certificateHash') {
        throw 'Certificate changed before it could be trusted.'
    }
    Import-Certificate -FilePath '$escapedPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    exit 0
} catch {
    Write-Error `$_ -ErrorAction Continue
    exit 1
}
"@
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($trustCommand))
    $process = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $encodedCommand `
        -Verb RunAs -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Certificate trust failed (exit code $($process.ExitCode)). The app was not installed."
    }
}

$signature = Get-AuthenticodeSignature -LiteralPath $bundlePath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
    throw "Installer signature verification failed: $($signature.StatusMessage)"
}

Add-AppxPackage -Path $bundlePath
Write-Host 'Dev Drive Bench is installed. Launch it from Start.'
