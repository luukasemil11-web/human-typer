@echo off
REM Double-click to run on Windows. First run sets up a private Python environment with pynput.
cd /d "%~dp0"
if not exist .venv (
  echo First run: setting up...
  py -3 -m venv .venv 2>nul || python -m venv .venv || goto :fail
  .venv\Scripts\pip install -q pynput || goto :fail
)
.venv\Scripts\python typer.py
exit /b
:fail
echo Setup failed. Install Python from python.org (tick "Add python.exe to PATH") and try again.
pause
