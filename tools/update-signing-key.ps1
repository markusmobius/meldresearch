#requires -Version 7.0
<#
.SYNOPSIS
Manages the offline ECDSA P-256 key that signs desktop update manifests.

.DESCRIPTION
The private key is a passphrase-encrypted PKCS#8 PEM kept by the deploying user (mode 0600), by default
~/.config/meldresearch/update-signing-key.pem on the server. It never enters the repository and must not be
readable by the web service account. Only the public key is committed, to
apps/LocalBackend/update-signing-public-key.txt, and stamped into every desktop package. The encrypted PEM is
itself the backup: keep a copy offline. Losing it means installed clients cannot be updated automatically.

.EXAMPLE
pwsh tools/update-signing-key.ps1                      # verify the installed key and passphrase
pwsh tools/update-signing-key.ps1 -Install ./key.pem   # install an encrypted key on the server
pwsh tools/update-signing-key.ps1 -Create              # new key (changes the trusted public key)
.\tools\update-signing-key.ps1 -ConvertWindowsKey .\update-signing-key.pem   # one-time move off Windows DPAPI
#>
[CmdletBinding(DefaultParameterSetName = 'Verify')]
param(
    [Parameter(ParameterSetName = 'Create')][switch]$Create,
    [Parameter(ParameterSetName = 'Install', Mandatory)][string]$Install,
    [Parameter(ParameterSetName = 'Convert', Mandatory)][string]$ConvertWindowsKey
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$root = Split-Path -Parent $PSScriptRoot
$keyPath = Get-UpdateSigningKeyPath
$publicPath = Get-UpdatePublicKeyPath $root

switch ($PSCmdlet.ParameterSetName) {
    'Create' {
        if (Test-Path -LiteralPath $keyPath) { throw "A signing key already exists at $keyPath. It was not replaced." }
        Write-Warning 'A new key replaces the trusted public key. Installed clients only accept releases signed by the key they were built with.'
        $key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
        try {
            Save-UpdateSigningKey $key $keyPath (Read-SigningPassphrase 'New passphrase' -Confirm)
            [IO.File]::WriteAllText($publicPath, [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()) + "`n", [Text.UTF8Encoding]::new($false))
        }
        finally { $key.Dispose() }
        Write-Host "Created the encrypted update signing key: $keyPath"
        Write-Host "Wrote the public key: $publicPath (commit this file). Keep an offline copy of the key file and its passphrase."
    }
    'Install' {
        if (Test-Path -LiteralPath $keyPath) { throw "A signing key already exists at $keyPath. It was not replaced." }
        $key = [Security.Cryptography.ECDsa]::Create()
        try {
            $pem = Get-Content -LiteralPath $Install -Raw
            try { $key.ImportFromEncryptedPem($pem, (Read-SigningPassphrase)) }
            catch { throw 'Wrong passphrase, or this is not an encrypted update signing key.' }
            Assert-TrustedSigningKey $key $root
            Save-UpdateSigningKey $key $keyPath (Read-SigningPassphrase 'Passphrase to protect the installed key (may be the same)' -Confirm)
        }
        finally { $key.Dispose() }
        Write-Host "Installed the update signing key for $([Environment]::UserName): $keyPath"
        Write-Host "Delete the copied file ($Install) if it is not your offline backup."
    }
    'Convert' {
        if (-not $IsWindows) { throw '-ConvertWindowsKey reads the earlier Windows DPAPI key and must run on that Windows account.' }
        $legacy = [IO.Path]::Combine([Environment]::GetFolderPath('ApplicationData'), 'MeldResearch', 'update-signing-key.dpapi')
        if (-not (Test-Path -LiteralPath $legacy)) { throw "No Windows DPAPI signing key at $legacy." }
        if (Test-Path -LiteralPath $ConvertWindowsKey) { throw "$ConvertWindowsKey already exists. It was not replaced." }
        $pkcs8 = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($legacy), $null, 'CurrentUser')
        $key = [Security.Cryptography.ECDsa]::Create()
        try {
            $key.ImportPkcs8PrivateKey($pkcs8, [ref]$null)
            Assert-TrustedSigningKey $key $root
            Save-UpdateSigningKey $key ([IO.Path]::GetFullPath($ConvertWindowsKey)) (Read-SigningPassphrase 'New passphrase' -Confirm)
        }
        finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($pkcs8); $key.Dispose() }
        Write-Host "Wrote the encrypted signing key: $ConvertWindowsKey"
        Write-Host 'Copy it to the server (scp), run tools/update-signing-key.ps1 -Install there, keep one offline copy,'
        Write-Host "then delete the Windows DPAPI copy: $legacy"
    }
    default {
        $key = Open-UpdateSigningKey -Root $root
        $key.Dispose()
        Write-Host "The update signing key at $keyPath opens with this passphrase and matches $publicPath."
    }
}
