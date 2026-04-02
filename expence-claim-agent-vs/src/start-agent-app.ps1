d:
cd D:\GitHub\anjumrizwi\ecommerce-app\src

# Base path
$BASE_PATH = "D:\GitHub\anjumrizwi\ecommerce-app\src"

Write-Host "Starting Ecommerce API..." -ForegroundColor Cyan

Start-Process powershell -ArgumentList @"
cd '$BASE_PATH'
dotnet run --project Ecommerce.API/Ecommerce.API.csproj
"@

Write-Host "Starting React UI..." -ForegroundColor Cyan

Start-Process powershell -ArgumentList @"
cd '$BASE_PATH\Ecommerce.UI\ClientApp'
npm run dev
"@

Write-Host "All services started successfully." -ForegroundColor Green