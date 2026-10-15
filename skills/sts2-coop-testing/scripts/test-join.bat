@echo off
rem Starts one more local test player and joins the host started by test-coop.bat.
rem Usage: test-join.bat [clientId]   (default 1002; use 1003, 1004 for a third or fourth player)
set "GAME=C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
set "ID=%~1"
if "%ID%"=="" set "ID=1002"
start "" /d "%GAME%" "%GAME%\SlayTheSpire2.exe" --force-steam=off --fastmp=join --clientId=%ID%
