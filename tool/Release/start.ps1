$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:ASPNETCORE_ENVIRONMENT = 'Production'
if ($env:SDATA_FILE) {
    if (-not (Test-Path -LiteralPath $env:SDATA_FILE -PathType Leaf)) { throw 'SDATA_FILE does not exist' }
} elseif (Test-Path -LiteralPath 'configs/system.json') {
    $env:SDATA_FILE = Join-Path $PSScriptRoot 'configs/system.json'
} else {
    $password = $env:SDATA_INITIAL_ADMIN_PASSWORD
    if (-not $password) {
        $secure = Read-Host 'Initial admin password (at least 12 characters)' -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    }
    if ($password.Length -lt 12) { throw 'Set SDATA_INITIAL_ADMIN_PASSWORD (at least 12 characters)' }
    $config = Get-Content -LiteralPath 'configs/system.example.json' -Raw | ConvertFrom-Json
    $config.SData.Auth.InitialAdminPassword = $password
    $json = $config | ConvertTo-Json -Depth 64
    $path = Join-Path $PSScriptRoot 'configs/system.json'
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
    $env:SDATA_FILE = $path
    $password = $null
}
Remove-Item Env:SDATA_INITIAL_ADMIN_PASSWORD -ErrorAction SilentlyContinue
& (Join-Path $PSScriptRoot 'SData.exe') @args
exit $LASTEXITCODE
