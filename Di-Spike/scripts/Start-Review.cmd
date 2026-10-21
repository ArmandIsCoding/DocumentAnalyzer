@echo off
setlocal
set "INVOICE_ROOT=%~dp0"
set "ASPNETCORE_ENVIRONMENT=Production"
set "DOTNET_CLI_UI_LANGUAGE=en"
pushd "%~dp0web"
"%~dp0web\InvoiceExplorer.Web.exe" --contentRoot "%~dp0web" --urls http://localhost:5180
set "reviewExit=%errorlevel%"
popd
exit /b %reviewExit%
