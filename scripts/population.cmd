@echo off
rem Fake players for the server running on this PC (docs/guide/population.md). Start the server first (start.cmd),
rem then this; any option goes through, e.g. population.cmd --bots 60
dotnet build "%~dp0..\src\Ro3Emu.Population" -c Release -v q -nologo -clp:ErrorsOnly || (pause & exit /b 1)
"%~dp0..\src\Ro3Emu.Population\bin\Release\net10.0\Ro3Emu.Population.exe" %*
