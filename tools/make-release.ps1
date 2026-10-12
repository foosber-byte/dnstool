param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [string]$PrivateKeyPath = "$env:USERPROFILE\.dnstool\release-key-private.xml"
)

$ErrorActionPreference = 'Stop'
$PublishDir = $PublishDir.TrimEnd('\')

$version = [regex]::Match(
    (Get-Content (Join-Path $PSScriptRoot '..\AppVersion.cs') -Raw),
    '"(\d+\.\d+\.\d+)"').Groups[1].Value
if (-not $version) { throw 'Не удалось прочитать версию из AppVersion.cs' }

$files = @('DNS Server Tool.exe', 'DNS Server Tool.exe.config', 'banner.png', 'System.Management.Automation.dll') |
    ForEach-Object { Join-Path $PublishDir $_ }
$missing = @($files | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing) { throw "В публикации нет файлов: $($missing -join ', ')" }

$zip = Join-Path $PublishDir "DNS.Server.Tool_v$($version)_x64.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $files -DestinationPath $zip

if (-not (Test-Path -LiteralPath $PrivateKeyPath)) {
    throw "Приватный ключ подписи не найден: $PrivateKeyPath. Без него клиенты откажутся ставить обновление."
}
$rsa = [System.Security.Cryptography.RSA]::Create()
$rsa.FromXmlString((Get-Content -LiteralPath $PrivateKeyPath -Raw))
$sig = [Convert]::ToBase64String($rsa.SignData(
    [IO.File]::ReadAllBytes($zip),
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1))
[IO.File]::WriteAllText("$zip.sig", $sig)

"Релиз v$($version) готов: $zip (+ .sig)"
