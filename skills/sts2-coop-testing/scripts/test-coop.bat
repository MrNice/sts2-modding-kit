@echo off
rem Two-player co-op test of a Slay the Spire 2 mod on ONE PC, no second Steam account needed.
rem Uses the game's built-in developer launch options: Steam off (offline profiles 1001 and 1002) and local
rem co-op on 127.0.0.1. The built mod is copied into the game's mods folder for the test and removed again when
rem both games close.
rem
rem Edit these three lines:
set "GAME=C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
set "MOD=MyMod"
rem Folder holding the built MyMod.dll and MyMod.json, relative to this script:
set "BUILD=bin\mod"

title %MOD% local co-op test - close both games to clean up
cd /d "%~dp0"
tasklist /fi "imagename eq SlayTheSpire2.exe" 2>nul | find /i "SlayTheSpire2.exe" >nul && (echo Close Slay the Spire 2 first. & pause & exit /b 1)
if not exist "%BUILD%\%MOD%.dll" (echo Build the mod first: %BUILD%\%MOD%.dll not found. & pause & exit /b 1)
xcopy /y /i /q "%BUILD%\*" "%GAME%\mods\%MOD%\" >nul

echo Starting host (player 1001)...
start "" /d "%GAME%" "%GAME%\SlayTheSpire2.exe" --force-steam=off --fastmp=host --clientId=1001
timeout /t 20 /nobreak >nul
echo Starting client (player 1002)...
start "" /d "%GAME%" "%GAME%\SlayTheSpire2.exe" --force-steam=off --fastmp=join --clientId=1002

echo.
echo Pick characters in each window, then start the run from the host.
echo If either game asks to enable mods, choose NOT to restart: close both games and run this again.
echo Leave this window open; it removes the test copy of the mod when both games close.
:wait
timeout /t 5 /nobreak >nul
tasklist /fi "imagename eq SlayTheSpire2.exe" 2>nul | find /i "SlayTheSpire2.exe" >nul && goto wait
rmdir /s /q "%GAME%\mods\%MOD%"
echo Cleaned up.
timeout /t 3 >nul
