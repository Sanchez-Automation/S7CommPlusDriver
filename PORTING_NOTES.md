# PORTING_NOTES

## Scope

Port target: `external/S7CommPlusDriver` to .NET 8-compatible SDK-style projects for Linux execution, preserving S7CommPlus protocol behavior and LGPL headers.

Out of scope:
- WinForms GUI migration (`src/S7CommPlusGUIBrowser`).
- Protocol object model redesign.

## Branching

Created and checked out `GHCopilot` in:
- superproject: `s7-driver`
- submodule: `external/S7CommPlusDriver`

## Project Retargeting (.NET 8)

Converted to SDK-style `net8.0`:
- `src/Zlib.net/Zlib.net.csproj`
- `src/S7CommPlusDriver/S7CommPlusDriver.csproj`
- `src/DriverTest/DriverTest.csproj`

Notes:
- Kept legacy `AssemblyInfo.cs` metadata by setting `GenerateAssemblyInfo=false`.
- Removed .NET Framework-specific imports/references and Windows-only post-build DLL copy usage.
- WinForms GUI project remains unported and excluded from Linux validation path.

### Solution file fix (`S7CommPlusDriver.sln`)

The original `.sln` only mapped `Debug|x64` / `Debug|x86` to `Build.0` for each
project — there was no `Any CPU` build mapping. Since `dotnet build`/`dotnet restore`
default to the `Any CPU` solution configuration when no `-p:Platform` is passed, a
plain `dotnet build S7CommPlusDriver.sln` matched zero projects and silently reported
"Build succeeded, 0 errors" while compiling nothing (`warning : Unable to find a
project to restore!`). `S7CommPlusGUIBrowser` (still net472/WinForms, unbuildable on
Linux) being present in the `.sln` made this easy to miss.

Fixed by regenerating the `.sln` from scratch (`dotnet new sln` + `dotnet sln add`)
containing only the three portable net8.0 projects — `S7CommPlusDriver`, `DriverTest`,
`Zlib.net`. None of them declare a `RuntimeIdentifier`/`Platform` constraint anymore
(that requirement existed only for the native OpenSSL P/Invoke DLLs, now removed), so
a clean `Any CPU` solution is sufficient. `S7CommPlusGUIBrowser`'s source is untouched
on disk, just no longer referenced by the solution.

Verified: `dotnet build S7CommPlusDriver.sln` now actually compiles all three projects
(0 errors; only the pre-existing SHA1-obsolete warnings in `Legitimation.cs` remain).

## TLS Layer Replacement

### Removed legacy OpenSSL backend

Deleted:
- `src/S7CommPlusDriver/OpenSSL/Native.cs`
- `src/S7CommPlusDriver/OpenSSL/OpenSSLConnector.cs`
- `src/S7CommPlusDriver/OpenSSL-dll-x64/`
- `src/S7CommPlusDriver/OpenSSL-dll-x86/`

### Added .NET TLS connector

Added:
- `src/S7CommPlusDriver/Net/TlsConnector.cs`

Implementation summary:
- Uses `SslStream` with TLS `1.2 | 1.3` enabled.
- Preserves permissive certificate trust model (accept all server certificates) to match previous runtime behavior.
- Mirrors original OpenSSL BIO pattern: each `ReadCompleted` call feeds one encrypted ISO payload and immediately decrypts synchronously, so no background reader thread is needed.
  - The background-reader-thread approach caused Browse timeouts because TLS 1.3 post-handshake messages (NewSessionTicket / KeyUpdate) cause `SslStream.Read()` to return 0 bytes, which silently killed the reader thread before any Browse response could be delivered.
- Includes OMS key material retrieval strategy:
  1. Use `SslStream` exporter API via reflection when available.
  2. Fallback to SHA-256 of TLS channel binding (`tls-unique`) when exporter API is not exposed by runtime.

### S7Client integration changes

Updated:
- `src/S7CommPlusDriver/Net/S7Client.cs`

Changes:
- Replaced OpenSSL connector integration with `TlsConnector`.
- Kept connect sequence ordering unchanged:
  1. TCP connect
  2. ISO/COTP connect
  3. unencrypted `InitSslRequest/InitSslResponse`
  4. TLS activate
- Kept ISO packet framing (`SendIsoPacket` / `RecvIsoPacket`) intact.
- `Send()` continues to send protocol payload through ISO path while TLS encryption/decryption occurs in connector tunnel.

## DriverTest symbolic read/write verification flow

Updated:
- `src/DriverTest/Program.cs`

New flow:
1. Connect
2. Browse symbols
3. For each configured symbol: read -> mutate -> write -> read-back verify
4. Best-effort restore original value
5. Exit code 0 on full pass, 1 on any failure

Default symbol list (customizable via 4th CLI arg as comma-separated names):
- `Main.BoolVar`
- `Main.IntVar`
- `Main.DIntVar`
- `Main.RealVar`
- `Main.LRealVar`
- `Main.StringVar`

Example run:

```bash
dotnet run --project src/DriverTest/DriverTest.csproj -- 10.0.0.20 "" "" "Main.BoolVar,Main.IntVar,Main.RealVar"
```

## Build Validation

Validated on Linux host:

```bash
cd external/S7CommPlusDriver/src
dotnet build DriverTest/DriverTest.csproj -c Release
```

Result:
- Build succeeded for `Zlib.net`, `S7CommPlusDriver`, `DriverTest` on `net8.0`.
- Remaining warnings are pre-existing SHA1 deprecation warnings in legitimation code.

## Network and Firmware Triage (PLCSIM)

Use probe-first troubleshooting before code-level debugging:

1. TCP reachability probe:

```bash
nc -vz 10.0.0.20 102
```

2. If probe fails:
- classify as network/firewall/routing issue.

3. If probe succeeds but TLS activation fails:
- classify as TLS policy / firmware capability mismatch.
- secure PG/HMI requires supported firmware family levels (notably >= 4.3/4.5 family-dependent baselines).

4. If TLS succeeds but read/write verify fails:
- classify as driver logic regression or PLC symbol/type mismatch.

## Compatibility Notes / Risks

- Certificate validation remains permissive for compatibility; this mirrors prior behavior but is not hardened trust.
- On runtimes lacking explicit `SslStream` key exporter API, OMS secret uses channel-binding-derived fallback. This may affect password-based legitimation interoperability on some PLC firmware variants; verify against target PLCSIM/CPU image.
- WinForms GUI project is intentionally excluded from Linux .NET 8 validation scope.

## Audit Trail Summary

Primary edited files:
- `src/S7CommPlusDriver/S7CommPlusDriver.csproj`
- `src/Zlib.net/Zlib.net.csproj`
- `src/DriverTest/DriverTest.csproj`
- `src/S7CommPlusDriver/Net/S7Client.cs`
- `src/S7CommPlusDriver/Net/TlsConnector.cs`
- `src/DriverTest/Program.cs`
- `src/S7CommPlusDriver.sln` (regenerated: removed `S7CommPlusGUIBrowser`, fixed `Any CPU` build mapping)

Primary deleted files/directories:
- `src/S7CommPlusDriver/OpenSSL/Native.cs`
- `src/S7CommPlusDriver/OpenSSL/OpenSSLConnector.cs`
- `src/S7CommPlusDriver/OpenSSL-dll-x64/`
- `src/S7CommPlusDriver/OpenSSL-dll-x86/`
