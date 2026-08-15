@echo off
REM Windows shim so the harness is one word from any shell:  tools\harness\fl status
REM Everything is delegated to fl.py; this file adds no behaviour of its own.
python "%~dp0fl.py" %*
