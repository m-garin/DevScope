@echo off
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0"

echo [1/6] Checking required files...
if not exist ".env.real" (
  echo ERROR: .env.real was not found in %CD%
  exit /b 1
)
if not exist "config\mcp.json" (
  echo ERROR: config\mcp.json was not found in %CD%
  exit /b 1
)

echo [2/6] Checking Docker Desktop...
docker info >nul 2>&1
if errorlevel 1 (
  echo ERROR: Docker Desktop is not running. Start it, wait for Engine running, and run this file again.
  exit /b 1
)

if /i "%~1"=="--check" (
  docker compose --env-file .env.real config --quiet
  if errorlevel 1 exit /b 1
  echo CHECK OK: files, Docker Desktop, and Compose configuration are available.
  exit /b 0
)

echo [3/6] Stopping old DevScope containers...
docker compose --env-file .env.real down --remove-orphans
if errorlevel 1 goto :failed

rem Remove only the obsolete container created by the previous manual launch instructions.
docker container inspect devscope-postgres-win >nul 2>&1
if not errorlevel 1 (
  echo Removing obsolete container devscope-postgres-win...
  docker rm -f devscope-postgres-win >nul
  if errorlevel 1 goto :failed
)

echo [4/6] Building and starting PostgreSQL and DevScope...
docker compose --env-file .env.real up -d --build
if errorlevel 1 goto :failed

echo [5/6] Waiting for PostgreSQL and DevScope...
set /a ATTEMPT=0
:wait_loop
set /a ATTEMPT+=1
set "POSTGRES_ID="
set "DEVSCOPE_ID="
for /f %%I in ('docker compose --env-file .env.real ps -q postgres') do set "POSTGRES_ID=%%I"
for /f %%I in ('docker compose --env-file .env.real ps -q devscope') do set "DEVSCOPE_ID=%%I"

set "POSTGRES_HEALTH="
set "DEVSCOPE_STATUS="
if defined POSTGRES_ID for /f %%I in ('docker inspect --format "{{.State.Health.Status}}" !POSTGRES_ID! 2^>nul') do set "POSTGRES_HEALTH=%%I"
if defined DEVSCOPE_ID for /f %%I in ('docker inspect --format "{{.State.Status}}" !DEVSCOPE_ID! 2^>nul') do set "DEVSCOPE_STATUS=%%I"

if /i "!POSTGRES_HEALTH!"=="healthy" if /i "!DEVSCOPE_STATUS!"=="running" goto :verify
if !ATTEMPT! GEQ 60 (
  echo ERROR: Containers did not become ready in 120 seconds.
  goto :failed
)
timeout /t 2 /nobreak >nul
goto :wait_loop

:verify
echo [6/6] Checking HTTP endpoints...
curl.exe --fail --silent --show-error http://127.0.0.1:8080/api/health >nul
if errorlevel 1 goto :failed

curl.exe --fail --silent --show-error http://127.0.0.1:8080/api/configuration/mcp/tools
if errorlevel 1 goto :failed
echo.

echo.
echo SUCCESS: DevScope and PostgreSQL are running.
echo Open http://127.0.0.1:8080
echo.
docker compose --env-file .env.real ps
exit /b 0

:failed
echo.
echo ERROR: DevScope did not start correctly. Recent logs:
docker compose --env-file .env.real ps
docker compose --env-file .env.real logs --tail=100 devscope postgres
exit /b 1
