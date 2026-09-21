# Aqorin.Phone

A compact cross-platform SIP softphone (Windows, macOS, Linux, Android, and iOS) for home routers and local PBXes that expose a
standard SIP registrar. Built with .NET 10, C#, Avalonia UI 12 (MVVM), SIPSorcery for SIP/SDP/RTP and PortAudio
for microphone/speaker access.

MVP scope: register/unregister, registration status, outgoing audio calls, incoming call detection with
answer/reject, hang-up, two-way G.711 audio, clear call-state and error reporting.

> **Router management APIs are not part of this milestone.** Aqorin.Phone uses pure SIP: REGISTER/INVITE/BYE
> signalling and RTP audio flow directly between the softphone and the configured registrar, exactly as with any
> IP telephone.

---

## 1. Architecture overview

```
Aqorin.Phone.sln
├─ src/Aqorin.Phone.Core      domain model, state machines, dial plan, redaction, application-owned interfaces
├─ src/Aqorin.Phone.Sip       SIPSorcery adapters: registration, call signalling, RTP media session
├─ src/Aqorin.Phone.Audio     PortAudio implementation of IAudioDeviceService (capture/playback streams)
├─ src/Aqorin.Phone.App       Avalonia UI, view models, DI composition root, settings store
├─ src/Aqorin.Phone.Mobile    Android/iOS Avalonia host; Android native audio implementation
└─ tests/
   ├─ Aqorin.Phone.Core.Tests   state machines, dial plan, validation, redaction, audio helpers, view models
   ├─ Aqorin.Phone.Sip.Tests    registration & call services driven through fake SIP seams
   └─ Aqorin.Phone.IntegrationTests   opt-in tests against a real SIP router
```

Key abstractions (all in `Aqorin.Phone.Core.Abstractions`, no SIPSorcery types leak into the UI):

| Interface | Responsibility | Implementation |
|-----------|----------------|----------------|
| `ISipRegistrationService` | register / unregister, refresh before expiry, bounded exponential retry, re-register on network change | `SipRegistrationService` (Sip) |
| `ICallService` | single call slot: place / answer / reject / hang up, `CallInfo` snapshots | `SipCallService` (Sip) |
| `IAudioMediaSessionFactory` | one RTP+codec+device session per call | `SipSorceryAudioMediaSessionFactory` (Sip) |
| `IAudioDeviceService` | device enumeration, PCM16 mono capture/playback streams | `PortAudioDeviceService` (Audio), `NullAudioDeviceService` (tests) |
| `IUiDispatcher`, `ISettingsStore`, `IDiagnosticsSwitch` | UI-thread marshalling, non-secret settings persistence, verbose logging toggle | App |

State is explicit: `RegistrationState { Disconnected, Registering, Registered, Unregistering, Failed }` and
`CallState { Idle, Dialing, Ringing, Incoming, Connecting, Active, Ending, Failed }`. `CallStateMachine` /
`RegistrationStateMachine` hold the transition tables and guards (no call before registration, no second call,
no answer without an incoming call, no hang-up without a call, no double registration). Services publish
immutable `RegistrationStatus` / `CallInfo` records; view models marshal them to the UI thread.

Inside `Aqorin.Phone.Sip`, SIPSorcery is wrapped behind small internal seams (`ISipTransportFactory`,
`ISipRegistrationClient`, `ISipUserAgent`, `ISipIncomingCall`) so the service logic is unit-tested with fakes.

Audio path: microphone → `IAudioCaptureStream` (PortAudio, resampled to 8 kHz, 20 ms frames) →
`AppAudioSource` (G.711 encode) → SIPSorcery `VoIPMediaSession` → RTP → the SIP router/PBX, and the reverse for
playback.
See [docs/audio-backend.md](docs/audio-backend.md) for the backend selection, verification and packaging details.

## 2. Router setup

Create or enable an IP telephone/SIP extension on your router or PBX, then enter its registrar host, SIP port,
transport, username, and password in **Settings**. Aqorin.Phone offers router profiles for generic SIP routers,
FRITZ!Box, Speedport, Vodafone Station, Livebox, Freebox, and custom PBX setups; the SIP defaults are port `5060`
with UDP, with TCP available when UDP is filtered.

### FRITZ!Box example

1. Open `http://fritz.box`, go to **Telephony › Telephony Devices › Configure New Device**.
2. Choose **Telephone (with or without answering machine)** › **LAN/WLAN (IP telephone)**.
3. Give it a name (e.g. *Aqorin.Phone*). The FRITZ!Box assigns an internal number such as **620** — this is
   the SIP **username**. Set a **password** (this is the SIP password).
4. Choose which outgoing number the phone uses and which incoming numbers it should ring for.
5. In the device's settings, allow **registration from the home network** (default). Registration from the
   internet is not needed for this MVP.
6. If you use a FRITZ!Box firmware that hides the *IP telephone* option, enable **Advanced View** first.

The registrar is `fritz.box` (or the router's LAN IP). Port `5060`, transport UDP (TCP is also supported).

## 3. Development prerequisites

* .NET SDK **10.0** (`global.json` pins 10.0.1xx with roll-forward).
* Windows 10/11, macOS 12+ or a desktop Linux (X11/Wayland) for the Avalonia UI.
* Linux build/test machines need `libasound2` and `libjack0` (see §6).
* No IDE is required; Visual Studio 2022 17.14+, Rider 2025+ and VS Code with C# Dev Kit all open the solution.

```bash
git clone <repo> Aqorin.Phone
cd Aqorin.Phone
dotnet restore
dotnet build
```

## 4. Running the application

```bash
dotnet run --project src/Aqorin.Phone.App
```

Build the Android application from the same solution:

```bash
dotnet build src/Aqorin.Phone.Mobile/Aqorin.Phone.Mobile.csproj -f net10.0-android
```

The iOS target requires Apple tooling and signing. Android uses its native capture/playback service; iOS audio
capture/playback is not implemented yet.

On first start the **Settings** tab is shown. Diagnostics (redacted log, audio devices) are in the expander at
the bottom of the window.

## 5. Configuring SIP credentials

Settings fields:

| Field | Default | Notes |
|-------|---------|-------|
| Router profile | `Generic SIP router` | keeps vendor-specific notes with the saved account |
| Registrar / host | `fritz.box` | host name or LAN IP; never hard-coded |
| Port | `5060` | |
| Transport | `Udp` | `Tcp` also supported |
| Username | – | SIP user/extension, e.g. `620` |
| Password | – | masked; saved **encrypted** when *Remember password* is on (default), never logged |
| Display name | – | optional, used in the From header |
| Microphone / speaker | system default | persisted device preference; refresh rescans PortAudio devices |
| Registration expiry | `300` s | 60–7200; refresh happens automatically at ~85 % |
| Local SIP port (Advanced) | `0` (automatic) | |
| Diagnostic logging (Advanced) | off | full SIP traces with Authorization/digest values redacted |

Settings are saved to `settings.json` under the per-user application-data folder
(`%APPDATA%\Aqorin.Phone`, `~/.config/Aqorin.Phone`, `~/Library/Application Support/Aqorin.Phone`).
With *Remember password* enabled the password is stored in the same file **encrypted, never as plaintext**:
on Windows with DPAPI (bound to your Windows user account), on macOS/Linux with AES-256-GCM using a random key
in `.credential-key` (file mode 0600) next to the settings. Unticking *Remember password* removes the stored
password immediately. See [docs/architecture-notes.md](docs/architecture-notes.md) for the threat model.

Dialling: the **Dialer** accepts telephone numbers (`030 123456`, `+49 30 123456` → `004930123456`),
internal numbers (`**620`), service codes (`*#06#`) and full SIP addresses (`sip:620@fritz.box`,
`620@fritz.box`). Normalisation lives in `DialPlan` and is unit-tested.

## 6. Platform-specific audio prerequisites

The app uses PortAudio through `PortAudioSharp2`; the native library is inside the NuGet package for
`win-x64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64` — nothing to install on Windows.

**Linux**

```bash
# Debian / Ubuntu
sudo apt-get install libasound2t64 libjack0      # older releases: libasound2 libjack0
# Fedora
sudo dnf install alsa-lib jack-audio-connection-kit
```

PulseAudio/PipeWire desktops work through their ALSA compatibility layer. The Avalonia UI additionally needs
the usual X11/fontconfig libraries that every desktop distribution already has (`libx11-6 libice6 libsm6
libfontconfig1`).

**macOS**

* The first time the softphone captures audio, macOS asks for **microphone permission** for the process
  that launched it (Terminal, iTerm, Rider, …). Grant it under *System Settings › Privacy & Security ›
  Microphone*. If it was denied once, re-enable it there and restart the app.
* A distributable `.app` bundle must declare `NSMicrophoneUsageDescription` in `Info.plist` and be signed;
  this repository publishes a plain folder, which is fine for development.
* If Gatekeeper quarantines a downloaded publish folder: `xattr -dr com.apple.quarantine Aqorin.Phone/`.

**Windows**

* Windows 10/11: the app uses WASAPI/WDM-KS/MME through PortAudio. Check *Settings › Privacy › Microphone*
  allows desktop apps to access the microphone.

If the native library cannot be loaded the app still starts and shows the reason in the status bar and the
Diagnostics panel instead of silently falling back to another backend.

## 7. Running the unit tests

```bash
dotnet test Aqorin.Phone.sln --filter "Category!=Integration"
```

Unit tests use fakes for SIP and audio and need neither a FRITZ!Box nor sound hardware. One smoke test
initialises the real PortAudio binding and passes on machines without any audio device (it asserts either a
successful initialisation or a precise unavailability reason).

`tests/Aqorin.Phone.Sip.Tests/Loopback` additionally runs the **real SIPSorcery signalling** against a fake
FRITZ!Box on `127.0.0.1` (`FakeFritzBox`, built on SIPSorcery's own UDP transport): digest-challenged REGISTER
(401 → authenticated 200 with the registrar's expiry), wrong-password handling, de-registration with
`Expires: 0`, an outgoing INVITE that is challenged, rings and ends with `486 Busy Here`, and an incoming
INVITE that rings the softphone and is rejected. These run as part of the normal test suite (category
`Loopback`) and only need loopback UDP.

## 8. Running the opt-in FRITZ!Box integration test

The tests in `tests/Aqorin.Phone.IntegrationTests` are **skipped unless** these environment variables are
set. They never contain credentials; export them in your shell:

```bash
export FRITZ_SIP_HOST=fritz.box
export FRITZ_SIP_PORT=5060
export FRITZ_SIP_USERNAME=620
export FRITZ_SIP_PASSWORD='…'
export FRITZ_SIP_DESTINATION='**1'     # optional: a number to ring, e.g. another internal phone
dotnet test tests/Aqorin.Phone.IntegrationTests --filter "Category=Integration" --logger "console;verbosity=detailed"
```

PowerShell: `$env:FRITZ_SIP_HOST='fritz.box'` etc. The tests register, check the wrong-password path
(expects an authentication failure without retries), place a call to `FRITZ_SIP_DESTINATION`, wait for
ringing/answer, hang up and unregister. All log output is redacted.

## 9. Publishing

```bash
./publish.sh
```

```powershell
./publish.ps1
```

Both scripts publish self-contained builds under the current version folder, for example:

```text
publish/0.1.1/win-x64
publish/0.1.1/linux-x64
publish/0.1.1/osx-x64
publish/0.1.1/osx-arm64
```

Pass one RID to publish a single platform: `./publish.sh linux-x64` or `./publish.ps1 -Rid win-x64`.
Use `SELF_CONTAINED=false` on Bash or `-FrameworkDependent` on PowerShell for framework-dependent builds.
The scripts also verify that the RID's native PortAudio library (`portaudio.dll`, `libportaudio.dylib`,
`libportaudio.so`) landed next to the executable.

The Windows installer definition lives at `installer/Aqorin.Phone.iss`. After publishing `win-x64`, build it
with Inno Setup:

```powershell
iscc "installer\Aqorin.Phone.iss" /DMyAppVersion=0.1.1 /DSourceDir="publish\0.1.1\win-x64"
```

or let the PowerShell publish script invoke Inno Setup when `iscc.exe` is on `PATH`:

```powershell
./publish.ps1 -BuildInstaller
```

The GitHub Actions workflow (`.github/workflows/build.yml`) restores, builds and runs the unit tests on
Ubuntu, Windows and macOS, then publishes all four RIDs and verifies the output.

## 10. Troubleshooting

**Second call "times out" / registration suddenly "does not respond" after a call worked**
* SIPSorcery processes all inbound SIP on a single thread; anything blocking there makes the phone deaf. Since
  v0.1.1 audio device open/close runs off that thread with timeouts and the registration retry re-opens the
  socket (see [docs/architecture-notes.md](docs/architecture-notes.md)). If you still see it, enable
  *Diagnostic logging*, reproduce, and check the log for `did not complete within` (a stuck audio driver) or
  `Re-opened the SIP transport`.

**Registration fails / stays "Registering"**
* `fritz.box does not point to your FRITZ!Box on this network` / the log shows `REGISTER` going to a **public** IP
  such as `212.42.244.122`: `.box` is a public top-level domain since 2024, so when your PC does not use the
  FRITZ!Box as DNS server (corporate VPN, custom DNS, DNS-over-HTTPS) the name resolves to a server on the
  internet that never answers. The app refuses to send anything in that case. Enter the FRITZ!Box LAN IP
  (usually `192.168.178.1`, see *Home Network › Network › Network Settings* on the box) as registrar, or make
  the FRITZ!Box your DNS server. Check with `nslookup fritz.box`.
* `Registration rejected: wrong username or password` (401/403): check the IP telephone's password in the
  FRITZ!Box; the username is the internal number (e.g. `620`), not the FRITZ!Box login.
* `Registrar fritz.box did not respond. Retrying in …`: the FRITZ!Box is unreachable or a firewall blocks
  UDP/TCP 5060 (see below). Retries use exponential backoff (3 s → 120 s, 8 attempts) and then give up. The status detail says whether the box replied at all (e.g. `401` then silence) — a FRITZ!Box that answered before and now stays silent has usually **blocked the phone temporarily after failed logins**: check *System › Event Log* on the FRITZ!Box and wait a few minutes before registering again.
* `Cannot resolve host fritz.box`: see *Hostname resolution*.
* `Could not open a local SIP socket`: another softphone is bound to the configured local port; leave the
  local port at `0`.
* Check *Telephony › Telephony Devices*: the device must be an **IP telephone** and registration from the
  home network must be allowed. Enable *Diagnostic logging* and read the redacted REGISTER/401/200 exchange.

**One-way audio / no audio**
* Both parties send RTP directly; if you hear nothing, the FRITZ!Box may be unable to reach the address the
  softphone advertised. Make sure the PC is in the FRITZ!Box LAN/WLAN (not a guest network) and not behind a
  VPN or a second router. Windows: allow the app through the firewall for private networks.
* Verify the negotiated codec in the Dialer (`Codec: PCMA`). The FRITZ!Box always supports G.711.
* Check the Diagnostics panel for `Microphone unavailable` / `Speaker unavailable` — the call continues with
  one-way audio in that case.
* macOS: grant microphone permission (see §6).

**No audio devices**
* Click *Audio devices* in the Diagnostics panel. If the backend reports *unavailable*, the native PortAudio
  library was not loaded: on Linux install `libasound2`/`libjack0`; make sure you ran/published for your
  platform's RID; on macOS remove the quarantine flag.
* Bluetooth headsets sometimes expose only a 16 kHz/48 kHz profile — the app resamples automatically.

**Firewall**
* Outbound UDP/TCP 5060 to the FRITZ!Box and inbound UDP replies on the ephemeral local SIP port.
* RTP: the media session binds random UDP ports (default range 10000–20000 area chosen by the OS); allow the
  app for the private/home network profile. Corporate firewalls on the PC often block inbound UDP — the
  symptom is one-way audio.

**Hostname resolution**
* `fritz.box` resolves only when the FRITZ!Box is the DNS server of the network. On VPNs, docker networks or
  when using public DNS (8.8.8.8), enter the router's LAN IP (usually `192.168.178.1`) as registrar instead.
* Test with `ping fritz.box` / `nslookup fritz.box`.

## 11. Known limitations of the MVP

* On macOS/Linux the remembered password is encrypted with a key file in the user profile rather than the
  OS keychain (Keychain/libsecret integration is a follow-up); on Windows DPAPI is used.
* Single call slot: no hold, transfer, conference or call waiting (a second incoming call is answered busy).
* Audio codecs: G.711 A-law and µ-law only; no echo cancellation, no jitter buffer beyond a bounded ring
  buffer, and a simple linear resampler.
* No DTMF sending UI, no ring tone playback, no call history.
* SIP over UDP/TCP only (no TLS/SRTP); IPv4 only.
* The app publishes as plain platform folders, plus an Inno Setup script for a per-user Windows installer. Signed
  macOS `.app` bundles and Linux packages are not part of this milestone.
* macOS and Linux behaviour was verified through static analysis and CI builds, not by a manual call on
  those systems (see the verification notes in [docs/audio-backend.md](docs/audio-backend.md)).
* TR-064 (call list, click-to-dial via the FRITZ!Box API) is intentionally out of scope.

## License

MIT for this repository. Third-party: SIPSorcery (BSD-3), Avalonia (MIT), PortAudioSharp2 and the
`org.k2fsa.portaudio.runtime.*` packages (Apache-2.0), PortAudio (MIT-style), CommunityToolkit.Mvvm (MIT).
