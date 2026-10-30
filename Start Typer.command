#!/bin/bash
# Double-click to run. First run sets up a private Python environment with pynput.
cd "$(dirname "$0")"
if [ ! -d .venv ]; then
  echo "First run: setting up..."
  python3 -m venv .venv && .venv/bin/pip install -q pynput || exit 1
fi
exec .venv/bin/python typer.py
