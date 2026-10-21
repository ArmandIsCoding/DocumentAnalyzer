@echo off
setlocal
set "INVOICE_ROOT=%~dp0"
set "DOTNET_CLI_UI_LANGUAGE=en"
"%~dp0cli\InvoiceExplorer.Cli.exe" %*
exit /b %errorlevel%
