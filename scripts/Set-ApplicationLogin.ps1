# PowerShell 5.1+ with .NET Framework 4.8, or PowerShell 7.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ApplicationDirectory,
    [string]$Username = 'usermcaroc@ct.com',
    [Security.SecureString]$Password,
    [switch]$Rotate
)
$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$target = Join-Path $directory 'appsettings.ApplicationLogin.json'
if ((Test-Path -LiteralPath $target) -and -not $Rotate) {
    Write-Host 'Application login already provisioned. No password changed. Use -Rotate to replace it.'
    return
}
if ($null -eq $Password) { $Password = Read-Host 'Initial application password' -AsSecureString }
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ([string]::IsNullOrWhiteSpace($plain)) { throw 'Password cannot be empty.' }
    $salt = [byte[]]::new(16)
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($salt) } finally { $random.Dispose() }
    $derivation = [Security.Cryptography.Rfc2898DeriveBytes]::new($plain, $salt, 100000, [Security.Cryptography.HashAlgorithmName]::SHA512)
    try { $derived = $derivation.GetBytes(32) } finally { $derivation.Dispose() }
    # ASP.NET Core Identity V3: marker, big-endian PRF/iterations/salt length, salt, subkey.
    $bytes = [byte[]]::new(61)
    $bytes[0] = 1
    $header = [byte[]](0,0,0,2, 0,1,134,160, 0,0,0,16)
    [Array]::Copy($header, 0, $bytes, 1, 12)
    [Array]::Copy($salt, 0, $bytes, 13, 16)
    [Array]::Copy($derived, 0, $bytes, 29, 32)
    @{ ApplicationAuth = @{ Username = $Username; PasswordHash = [Convert]::ToBase64String($bytes) } } |
        ConvertTo-Json | Set-Content -LiteralPath $target -Encoding utf8
    Write-Host 'Application login configured. Only the password hash was saved.'
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    $plain = $null
}
