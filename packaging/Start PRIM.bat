@echo off
REM ============================================================
REM  PRIM launcher — Physical Records Inventory Manager
REM  Starts the PRIM server and opens it in your browser.
REM ============================================================
cd /d "%~dp0"

REM Start the server minimized; the database is created on first run.
start "PRIM Server" /min Prim.exe

REM Give the server a few seconds to start, then open the browser.
timeout /t 6 /nobreak >nul
start "" "http://localhost:5000/"

echo PRIM is running at http://localhost:5000/
echo Close the "PRIM Server" window to stop it.
pause
