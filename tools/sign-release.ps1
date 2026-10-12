param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,

    [string]$PrivateKeyPath = "$env:USERPROFILE\.dnstool\release-key-private.xml"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PrivateKeyPath)) {
    throw "Приватный ключ не найден: $PrivateKeyPath"
}

$rsa = [System.Security.Cryptography.RSA]::Create()
$rsa.FromXmlString((Get-Content -LiteralPath $PrivateKeyPath -Raw))

$fullPath = (Resolve-Path -LiteralPath $ZipPath).Path
$bytes = [IO.File]::ReadAllBytes($fullPath)
$sig = [Convert]::ToBase64String($rsa.SignData(
    $bytes,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1))

$sigPath = "$fullPath.sig"
[IO.File]::WriteAllText($sigPath, $sig)

"Подписан: $fullPath"
"Подпись:  $sigPath"
