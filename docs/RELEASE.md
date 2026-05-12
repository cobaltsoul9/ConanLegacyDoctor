# Release Checklist

Use this checklist before publishing a downloadable Windows build.

## 1. Validate source

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Invoke-Pester .\tests"
dotnet build .\ConanLegacyDoctor.slnx -c Release
dotnet run --project .\tests\ConanLegacyDoctor.Core.Smoke\ConanLegacyDoctor.Core.Smoke.csproj -c Release
```

## 2. Publish the executable

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-win-x64.ps1
```

This produces:

- `artifacts\publish\win-x64\ConanLegacyDoctor.exe`
- `artifacts\publish\win-x64\SHA256SUMS.txt`

## 3. Sign before wide public distribution

Sign the executable with an Authenticode code-signing certificate owned by the publisher.

Example shape:

```powershell
signtool sign `
  /fd SHA256 `
  /tr http://timestamp.digicert.com `
  /td SHA256 `
  /a `
  .\artifacts\publish\win-x64\ConanLegacyDoctor.exe
```

Then verify:

```powershell
Get-AuthenticodeSignature .\artifacts\publish\win-x64\ConanLegacyDoctor.exe
```

## 4. Recompute checksum after signing

Signing changes the executable bytes. Regenerate `SHA256SUMS.txt` after the final signed binary exists.

## 5. Create the GitHub Release

Attach:

- `ConanLegacyDoctor.exe`
- `SHA256SUMS.txt`

Include release notes that summarize:

- the supported OS/runtime posture,
- the reversible action model,
- save quarantine behavior,
- any known limitations,
- the exact version tag.
