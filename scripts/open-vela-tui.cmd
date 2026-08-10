@echo off
cd /d D:\Jason\Documents\Workspace\vs2022\repo\Vela
"%TEMP%\vela-terminalgui-spike\dotnet10\dotnet.exe" run --project src\Vela.Tui\Vela.Tui.csproj --no-restore
pause
