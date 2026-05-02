$ErrorActionPreference = 'Continue'

$deadline = (Get-Date).AddSeconds(45)

while ((Get-Date) -lt $deadline) {

    try {

        $null = Invoke-WebRequest -Uri 'http://127.0.0.1:5001/' -TimeoutSec 1 -UseBasicParsing

        exit 0

    }

    catch {

        Start-Sleep -Seconds 1

    }

}

Write-Host 'Arcanum: timed out waiting for http://127.0.0.1:5001/. Start "Cli: serve (API on :5001)" or "Api.DevHost: slim API" first, then retry.' -ForegroundColor Red

exit 1
