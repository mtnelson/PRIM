@echo off
REM ============================================================
REM  RIM launcher — Records Inventory Manager
REM  Starts the RIM server and opens it in your browser.
REM ============================================================
cd /d "%~dp0"

REM Start the server minimized; the database is created on first run.
start "RIM Server" /min Rim.exe

REM Give the server a few seconds to start, then open the browser.
timeout /t 6 /nobreak >nul
start "" "http://localhost:5000/"

echo RIM is running at http://localhost:5000/
echo Close the "RIM Server" window to stop it.
pause
