$ErrorActionPreference = 'Stop'
$previousNativeErrorPreference = $PSNativeCommandUseErrorActionPreference
$PSNativeCommandUseErrorActionPreference = $false

try {
    dotnet dev-certs https --check --trust
    if ($LASTEXITCODE -ne 0) {
        dotnet dev-certs https --trust
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать доверенный HTTPS-сертификат localhost.' }
    }
}
finally {
    $PSNativeCommandUseErrorActionPreference = $previousNativeErrorPreference
}

Write-Host 'Локальный HTTPS готов: https://localhost:8443'
Write-Host 'Для тестового публичного адреса запустите:'
Write-Host 'cloudflared tunnel --url https://localhost:8443'
Write-Host 'Скопируйте выданный https://*.trycloudflare.com адрес в настройки webhook приложения.'
