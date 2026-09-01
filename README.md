# FalconRC — Falcon Radio Controller

A Windows + Android (.NET MAUI) control head for the Harris **AN/PRC-138**
(RT-1694 + RF-5122 ALE) HF manpack, operated over its RS-232 remote port.

## Features

- **SSB / ALE / HOP operation** — mode, power level, VFO frequency and
  channel selection, antenna-coupler tune with live status.
- **ALE** — scan, calls to individuals / nets / ANY / ALL, link banner,
  AMD messages (send and inbox), LQA reports, sounding, and schedules.
- **HOP** — hopping-net selection and sync, net info, exclusion bands.
- **Programming** — SSB channels, HOP nets and hop lists, the ALE address
  book and scan channel groups, and modem presets, each write verified by
  re-read.
- **Cloning** — read the radio's fill to a file; write it to another
  radio with ALE identity swap and a closing compare.
- **Console** — every line on the wire, raw command input, session export.
- **DEMO port** — explore the full app with no radio attached.

## Install

- **Windows**: [FalconRC-win-x64.zip](FalconRC-win-x64.zip) — extract
  anywhere, run `FalconRC.exe`. Self-contained; no .NET install needed.
- **Android**: [FalconRC.apk](FalconRC.apk) — sideload (allow "install
  unknown apps"). Android 7.0+ with USB host support.

The radio connects through an FTDI USB-RS232 cable (via USB OTG on
Android).

## Building

Requires the .NET 10 SDK with the `maui-windows` and `android` workloads.

```
dotnet build src/Falcon.App/Falcon.App.csproj -f net10.0-windows10.0.19041.0
dotnet build src/Falcon.App/Falcon.App.csproj -f net10.0-android
```

## Licensing

- Licensed under the **GNU GPL v3** — see [LICENSE](LICENSE). Copyright
  (c) cam-hosken.
- [libs/UsbSerialForAndroid.Net](libs/UsbSerialForAndroid.Net/) — vendored
  MIT fork of UsbSerialForAndroid.Net v1.0.6 (LUJIAN2020); its own LICENSE
  and upstream README are retained in that folder.
