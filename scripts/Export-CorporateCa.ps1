[CmdletBinding()]
param(
    [Parameter()]
    [string] $GatewayHostName = "ai-gateway-prod.sovcombank.group",

    [Parameter()]
    [string] $OutputDirectory = (Join-Path $PSScriptRoot "..\certs")
)

$ErrorActionPreference = "Stop"
$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $resolvedOutputDirectory | Out-Null

$tcpClient = $null
$sslStream = $null
$certificateChain = $null

try {
    $tcpClient = [System.Net.Sockets.TcpClient]::new($GatewayHostName, 443)
    $sslStream = [System.Net.Security.SslStream]::new($tcpClient.GetStream(), $false)
    $sslStream.AuthenticateAsClient($GatewayHostName)

    $serverCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $sslStream.RemoteCertificate)
    $certificateChain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    $certificateChain.ChainPolicy.RevocationMode =
        [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck

    if (-not $certificateChain.Build($serverCertificate)) {
        $statuses = $certificateChain.ChainStatus |
            ForEach-Object { $_.StatusInformation.Trim() } |
            Where-Object { $_ }
        throw "Windows could not build a trusted certificate chain: $($statuses -join '; ')"
    }

    if ($certificateChain.ChainElements.Count -lt 2) {
        throw "The server certificate chain does not contain a CA certificate."
    }

    for ($certificateIndex = 1; $certificateIndex -lt $certificateChain.ChainElements.Count; $certificateIndex++) {
        $certificate = $certificateChain.ChainElements[$certificateIndex].Certificate
        $temporaryCer = Join-Path $resolvedOutputDirectory "corporate-ca-$certificateIndex.cer"
        $outputCrt = Join-Path $resolvedOutputDirectory "corporate-ca-$certificateIndex.crt"

        [System.IO.File]::WriteAllBytes(
            $temporaryCer,
            $certificate.Export(
                [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

        & certutil.exe -f -encode $temporaryCer $outputCrt | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "certutil failed while converting $temporaryCer"
        }

        Remove-Item -LiteralPath $temporaryCer -Force
        $sha256Algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            $sha256 = ($sha256Algorithm.ComputeHash($certificate.RawData) |
                ForEach-Object { $_.ToString("X2") }) -join ""
        }
        finally {
            $sha256Algorithm.Dispose()
        }
        Write-Host "Exported $($certificate.Subject)"
        Write-Host "  Issuer:  $($certificate.Issuer)"
        Write-Host "  SHA-256: $sha256"
        Write-Host "  Expires: $($certificate.NotAfter.ToString('u'))"
    }

    Write-Host "Corporate CA certificates were written to $resolvedOutputDirectory"
}
finally {
    if ($certificateChain) { $certificateChain.Dispose() }
    if ($sslStream) { $sslStream.Dispose() }
    if ($tcpClient) { $tcpClient.Dispose() }
}
