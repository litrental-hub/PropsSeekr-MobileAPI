Write-Host "=================================================="
Write-Host "RUNNING DEPLOYMENT VIA DOTNET SCRATCH RUNNER"
Write-Host "=================================================="

dotnet run --project scratch\PaymentSignatureTests.csproj --roll-forward Major

Write-Host "=================================================="
