# AudioInOutTranscribing (V1)

WinForms tray app for Windows that captures microphone + speaker loopback audio, chunks WAV files, transcribes with Mistral, and writes per-source transcripts.

Transcript output options:
- `txt+jsonl` writes per-source files under `mic/` and `speaker/` (default).
- `txt+jsonl+merged` also writes a combined `transcript.txt` in the session root, with non-overlapping speaker IDs across mic + speaker streams.

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

By default, the script auto-increments the MSI `ProductVersion` from the latest built MSI in `AudioInOutTranscribing.Installer\bin\...`.

Optional explicit version override:

```powershell
.\tools\build-msi.ps1 -Version 1.0.1
```

## Install MSI with feedback

```powershell
.\tools\install-msi.ps1
```

The installer is launched with full UI (including the license prompt) and writes a verbose log to `artifacts\logs\`.

## License

This project is licensed under the Apache License 2.0. See `LICENSE`.
