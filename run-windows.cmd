@echo off
setlocal EnableExtensions

cd /d "%~dp0"
set "DEVSCOPE_ROOT=%CD%"
set "CLEAR_CACHE=0"
if /i "%~1"=="--clear-cache" set "CLEAR_CACHE=1"
if not "%~1"=="" if /i not "%~1"=="--clear-cache" (
  echo ERROR: Unknown option "%~1". Use --clear-cache or run without options.
  exit /b 1
)

echo [1/6] Checking configuration and tools...
if not exist ".env.real" (
  echo ERROR: .env.real was not found in %CD%
  exit /b 1
)
if not exist "config\mcp.json" (
  echo ERROR: config\mcp.json was not found in %CD%
  exit /b 1
)

where docker >nul 2>&1 || (echo ERROR: docker was not found.& exit /b 1)
where dotnet >nul 2>&1 || (echo ERROR: .NET SDK 10 was not found.& exit /b 1)
where npm >nul 2>&1 || (echo ERROR: Node.js and npm were not found.& exit /b 1)
dotnet --list-sdks | findstr /b "10." >nul || (echo ERROR: .NET SDK 10 was not found.& exit /b 1)
docker info >nul 2>&1 || (echo ERROR: Docker Desktop is not running.& exit /b 1)

for /f "usebackq eol=# tokens=1,* delims==" %%A in (".env.real") do if not "%%A"=="" set "%%A=%%B"
if not defined DEVSCOPE_PORT set "DEVSCOPE_PORT=8080"
if not defined DEVSCOPE_PROJECT_KEY set "DEVSCOPE_PROJECT_KEY=FINTDR"
if not defined DEVSCOPE_DEVELOPER_GROUP set "DEVSCOPE_DEVELOPER_GROUP=Fintender-Backend"
if not defined DEVSCOPE_DAILY_REFRESH_HOUR set "DEVSCOPE_DAILY_REFRESH_HOUR=3"
if not defined DEVSCOPE_DB_PORT set "DEVSCOPE_DB_PORT=5432"
if not defined DEVSCOPE_DB_NAME set "DEVSCOPE_DB_NAME=devscope"
if not defined DEVSCOPE_DB_USER set "DEVSCOPE_DB_USER=devscope"
if not defined DEVSCOPE_DB_PASSWORD set "DEVSCOPE_DB_PASSWORD=devscope-local-only"

echo [2/6] Stopping the Linux DevScope container...
docker compose --env-file .env.real stop devscope >nul 2>&1
docker compose --env-file .env.real rm -f devscope >nul 2>&1

echo [3/6] Starting PostgreSQL in Docker...
docker compose -f compose.yaml -f compose.windows.yaml --env-file .env.real up -d postgres
if errorlevel 1 goto :failed

echo Waiting up to 120 seconds for PostgreSQL...
setlocal EnableDelayedExpansion
set /a ATTEMPT=0
:wait_postgres
set /a ATTEMPT+=1
set "POSTGRES_ID="
for /f %%I in ('docker compose -f compose.yaml -f compose.windows.yaml --env-file .env.real ps -q postgres') do set "POSTGRES_ID=%%I"
set "POSTGRES_HEALTH="
if defined POSTGRES_ID for /f %%I in ('docker inspect --format "{{.State.Health.Status}}" !POSTGRES_ID! 2^>nul') do set "POSTGRES_HEALTH=%%I"
if not defined POSTGRES_HEALTH set "POSTGRES_HEALTH=container not found"
echo PostgreSQL health: !POSTGRES_HEALTH! ^(attempt !ATTEMPT!/60^)
if /i "!POSTGRES_HEALTH!"=="healthy" (
  endlocal
  goto :build_web
)
if !ATTEMPT! GEQ 60 (
  echo ERROR: PostgreSQL did not become healthy in 120 seconds.
  endlocal
  goto :failed
)
ping 127.0.0.1 -n 3 >nul
goto :wait_postgres

:build_web
echo [4/6] Building the web interface on Windows...
pushd "src\DevScope.Web"
call npm ci --prefer-offline
if errorlevel 1 (popd & goto :failed)
call npm run build
if errorlevel 1 (popd & goto :failed)
popd

if exist "src\DevScope.Api\wwwroot" rmdir /s /q "src\DevScope.Api\wwwroot"
xcopy /e /i /q /y "src\DevScope.Web\dist" "src\DevScope.Api\wwwroot" >nul
if errorlevel 1 goto :failed

echo [5/6] Configuring the Windows backend...
set "ASPNETCORE_URLS=http://127.0.0.1:%DEVSCOPE_PORT%"
set "DevScope__DataSource=Mcp"
set "DevScope__McpConfigPath=%DEVSCOPE_ROOT%\config\mcp.json"
set "DevScope__TeamsConfigPath=%DEVSCOPE_ROOT%\config\teams.json"
set "DevScope__ProjectKey=%DEVSCOPE_PROJECT_KEY%"
set "DevScope__DeveloperGroup=%DEVSCOPE_DEVELOPER_GROUP%"
set "DevScope__DailyRefreshHour=%DEVSCOPE_DAILY_REFRESH_HOUR%"
set "DevScope__ClearCacheOnStartup=%CLEAR_CACHE%"
set "ConnectionStrings__DevScope=Host=127.0.0.1;Port=%DEVSCOPE_DB_PORT%;Database=%DEVSCOPE_DB_NAME%;Username=%DEVSCOPE_DB_USER%;Password=%DEVSCOPE_DB_PASSWORD%"

echo [6/6] Starting DevScope on Windows...
if "%CLEAR_CACHE%"=="1" echo Cached activity reports will be cleared during startup. Application settings will be preserved.
echo Open http://127.0.0.1:%DEVSCOPE_PORT% after the Application started message.
echo Keep this window open. Press Ctrl+C to stop DevScope.
echo.
pushd "src\DevScope.Api"
dotnet run --no-launch-profile
set "DEVSCOPE_EXIT=%ERRORLEVEL%"
popd
exit /b %DEVSCOPE_EXIT%

:failed
echo.
echo ERROR: Windows launch failed.
docker compose -f compose.yaml -f compose.windows.yaml --env-file .env.real ps
exit /b 1
