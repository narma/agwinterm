@echo off
rem Explicit user action only; never called during terminal startup or setup.
winget install --id Microsoft.EdgeWebView2Runtime --exact --source winget
if errorlevel 1 (
  echo WebView2 installation did not finish. For offline installation, see:
  echo https://developer.microsoft.com/microsoft-edge/webview2/
  exit /b 1
)
