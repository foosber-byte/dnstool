# Проверка парности приватного ключа подписи релизов с публичным ключом,
# вшитым в UpdateChecker.cs. Запуск перед каждым релизом:
#   powershell -File tools\check-release-key.ps1
# Код возврата: 0 = ключи парные (можно подписывать),
#               1 = ключей нет / не парные (релиз не собирать).
# Синхронизация ключей между машинами: Nextcloud\secure\dnstool\
# (см. docs\HANDOFF.md).

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$checkerPath = Join-Path $repoRoot 'UpdateChecker.cs'
$keyPaths = @(
    (Join-Path $env:USERPROFILE '.dnstool\release-key-private.xml'),
    (Join-Path $env:USERPROFILE 'Nextcloud\secure\dnstool\release-key-private.xml')
)

$keyXml = $null
$keyUsed = $null
foreach ($p in $keyPaths) {
    if (Test-Path -LiteralPath $p) { $keyXml = Get-Content -LiteralPath $p -Raw; $keyUsed = $p; break }
}
if (-not $keyXml) {
    Write-Host "НЕТ КЛЮЧА: ни $env:USERPROFILE\.dnstool\release-key-private.xml, ни Nextcloud\secure\dnstool\release-key-private.xml не найдены."
    Write-Host "Возьми ключ из Nextcloud\secure\dnstool\ (см. docs\HANDOFF.md) и положи в ~\.dnstool\."
    exit 1
}

$src = Get-Content -LiteralPath $checkerPath -Raw
$match = [regex]::Match($src, 'private const string PublicKeyXml = "([^"]+)"')
if (-not $match.Success) {
    Write-Host "ОШИБКА: в $checkerPath не найден PublicKeyXml."
    exit 1
}
$embeddedXml = $match.Groups[1].Value

$priv = [System.Security.Cryptography.RSA]::Create()
$priv.FromXmlString($keyXml)
$privParams = $priv.ExportParameters($false)

$pub = [System.Security.Cryptography.RSA]::Create()
$pub.FromXmlString($embeddedXml)

$data = [Text.Encoding]::UTF8.GetBytes('dnstool-release-key-pairing-check')
$sig = $priv.SignData($data, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$paired = $pub.VerifyData($data, $sig, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

$mod = [Convert]::ToBase64String($privParams.Modulus)
Write-Host ("Ключ:           {0} (RSA {1} бит)" -f $keyUsed, ($privParams.Modulus.Length * 8))
Write-Host ("Modulus (префикс): {0}..." -f $mod.Substring(0, [Math]::Min(24, $mod.Length)))

if ($paired) {
    Write-Host 'OK: приватный ключ парен с PublicKeyXml в UpdateChecker.cs. Релиз собирать можно.'
    exit 0
}
else {
    Write-Host 'KEY ROTATED: ключ НЕ парен с вшитым PublicKeyXml. НЕ ПОДПИСЫВАТЬ и НЕ ВЫПУСКАТЬ релиз.'
    Write-Host 'Обнови ~\.dnstool\release-key-private.xml из Nextcloud\secure\dnstool\ или запроси ключ у владельца.'
    Write-Host 'Журнал передач: docs\HANDOFF.md.'
    exit 1
}
