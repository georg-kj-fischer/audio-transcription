# AudioInOutTranscribing (V1)

WinForms tray app for Windows that captures microphone + speaker loopback audio, chunks WAV files, transcribes with Mistral, and writes per-source transcripts.

## Projects

- `AudioInOutTranscribing.App` - .NET 8 WinForms tray application
- `AudioInOutTranscribing.Tests` - xUnit tests for settings, chunking, retry policy, and transcript writing
- `AudioInOutTranscribing.Installer` - WiX installer project that builds an MSI from published app files

## Build prerequisites

- .NET SDK 8.0+
- Windows 10/11 x64

## Run

```powershell
dotnet restore
dotnet run --project .\AudioInOutTranscribing.App\AudioInOutTranscribing.App.csproj
```

## Test

```powershell
dotnet test .\AudioInOutTranscribing.sln
```

## Self-contained publish

```powershell
dotnet publish .\AudioInOutTranscribing.App\AudioInOutTranscribing.App.csproj -c Release -r win-x64 --self-contained true
```

## Build MSI installer

```powershell
.\tools\build-msi.ps1
```

Optional version override:

```powershell
.\tools\build-msi.ps1 -Version 1.0.1
```

## Install MSI with feedback

```powershell
.\tools\install-msi.ps1
```

The installer is launched with basic UI (`/qb+`) and writes a verbose log to `artifacts\logs\`.
