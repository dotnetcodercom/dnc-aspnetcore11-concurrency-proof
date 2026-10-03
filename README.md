# ASP.NET Core 11 concurrency admission proof

A local runnable contract proof for the related DotNetCoder article.

## Tested versions

.NET SDK `11.0.100-rc.1.26425.128`; ASP.NET Core `11.0.0-rc.1.26425.128`; target `net11.0`. Keep `global.json` with the project. Uses the ASP.NET Core shared framework; no external package references.

## Prerequisites

Windows PowerShell and the pinned .NET SDK. First package restore needs access to the configured NuGet source. No Azure account, Docker, database or credentials are required.

## Run in your browser

```powershell
Get-ChildItem -Recurse -Filter *.ps1 | Unblock-File
powershell -NoProfile -ExecutionPolicy Bypass -File .\Start-Demo.ps1
```

Open http://127.0.0.1:5128, select **Run verification**, and expect **PASS · 11/11 checks**. Select **Download receipt** to preserve the run. Stop the server with Ctrl+C.

## Run verification without a browser

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Verify-Demo.ps1
```

The script restores and builds the application, runs the verifier, and fails on a failed check. Local evidence is written under `evidence/` and should not be committed.

## Scope

Two active handlers, one queued request, HTTP 429 overflow, permit recovery after completion and explicit HTTP 500 response, queued cancellation. No equivalence to legacy middleware, FIFO ordering among multiple waiters, exception path, performance or distributed limit is claimed.

The proof binds to loopback. Its control endpoints are test fixtures; do not deploy it as a public service. Application source files match the verified laptop package. The English README and gitignore are packaging additions. See README-AR.md for Arabic setup instructions.
