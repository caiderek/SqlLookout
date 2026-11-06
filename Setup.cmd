@echo off
rem Opens the SqlLookout setup wizard in your browser. Keep this window open until you are done.
"%~dp0sqllookout.exe" setup
if errorlevel 1 pause
