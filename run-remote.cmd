@echo off
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0"

echo [1/7] Checking required files...
if not exist ".env.real" (
  echo ERROR: .env.real was not found in %CD%
  exit /b 1
)
if not exist "config\mcp.json" (
  echo ERROR: config\mcp.json was not found in %CD%
  exit /b 1
)

echo [2/7] Checking Docker Desktop...
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

echo [3/7] Stopping old DevScope containers...
docker compose --env-file .env.real down --remove-orphans
if errorlevel 1 goto :failed

rem Remove only the obsolete container created by the previous manual launch instructions.
docker container inspect devscope-postgres-win >nul 2>&1
if not errorlevel 1 (
  echo Removing obsolete container devscope-postgres-win...
  docker rm -f devscope-postgres-win >nul
  if errorlevel 1 goto :failed
)

echo [4/7] Building DevScope...
docker compose --env-file .env.real build devscope
if errorlevel 1 goto :failed

echo [5/7] Starting PostgreSQL...
docker compose --env-file .env.real up -d postgres
if errorlevel 1 goto :failed

echo Waiting up to 120 seconds for PostgreSQL...
set /a ATTEMPT=0
:wait_postgres
set /a ATTEMPT+=1
set "POSTGRES_ID="
for /f %%I in ('docker compose --env-file .env.real ps -q postgres') do set "POSTGRES_ID=%%I"

set "POSTGRES_HEALTH="
if defined POSTGRES_ID for /f %%I in ('docker inspect --format "{{.State.Health.Status}}" !POSTGRES_ID! 2^>nul') do set "POSTGRES_HEALTH=%%I"

if /i "!POSTGRES_HEALTH!"=="healthy" goto :start_devscope
if !ATTEMPT! GEQ 60 (
  echo ERROR: PostgreSQL did not become healthy in 120 seconds.
  goto :failed
)
timeout /t 2 /nobreak >nul
goto :wait_postgres

:start_devscope
echo [6/7] Starting DevScope...
rem PostgreSQL is already healthy, so Compose does not need to wait on dependencies here.
docker compose --env-file .env.real up -d --no-deps devscope
if errorlevel 1 goto :failed

echo Waiting up to 120 seconds for the HTTP API...
set /a ATTEMPT=0
:wait_api
set /a ATTEMPT+=1
curl.exe --fail --silent http://127.0.0.1:8080/api/health >nul 2>&1
if not errorlevel 1 goto :verify
if !ATTEMPT! GEQ 60 (
  echo ERROR: DevScope HTTP API did not become ready in 120 seconds.
  goto :failed
)
timeout /t 2 /nobreak >nul
goto :wait_api

:verify
echo [7/7] Checking the real MCP connections...
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
