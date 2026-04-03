d:
cd D:\GitHub\anjumrizwi\ai-agents\expence-claim-agent-vs\src

# Base path
$BASE_PATH = "D:\GitHub\anjumrizwi\ai-agents\expence-claim-agent-vs\src"

Write-Host "Starting Ecommerce API..." -ForegroundColor Cyan

Start-Process powershell -ArgumentList @"
cd '$BASE_PATH'
dotnet run --project ExpenseClaim.Api/ExpenseClaim.Api.csproj
"@

Write-Host "Starting React UI..." -ForegroundColor Cyan

#Start-Process powershell -ArgumentList @"
#cd '$BASE_PATH\Ecommerce.UI\ClientApp'
#npm run dev
#"@

Write-Host "All services started successfully." -ForegroundColor Green