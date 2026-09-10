# Audio backend: PortAudio via PortAudioSharp2

## Why PortAudio

SIPSorcery's ready-made audio endpoint (`SIPSorceryMedia.Windows`) is Windows-only (NAudio/WASAPI).
For Windows + macOS + Linux the softphone uses **PortAudio**, a mature C library with native host
APIs on every desktop platform:

| Platform | PortAudio host API used by the bundled build |
|----------|----------------------------------------------|
| Windows  | WASAPI / WDM-KS / MME / DirectSound          |
| macOS    | CoreAudio (`libportaudio.dylib` links AudioToolbox, AudioUnit, CoreAudio, CoreFoundation, CoreServices) |
| Linux    | ALSA (`libasound.so.2`) and JACK (`libjack.so.0`) |

Binding: [`PortAudioSharp2`](https://www.nuget.org/packages/PortAudioSharp2) 1.0.6 (Apache-2.0). It is the
binding used by the sherpa-onnx project for real-time microphone capture and speaker playback and is
the only maintained .NET PortAudio binding that ships **pre-compiled native libraries** on NuGet.

Alternatives considered:

* `SIPSorceryMedia.SDL2` (LGPL-2.1) — provides SIPSorcery `IAudioSource`/`IAudioSink` directly, but the
  NuGet package does **not** contain SDL2 itself; SDL2 has to be installed or shipped separately on every OS.
* `SIPSorceryMedia.Windows` — Windows only. Explicitly *not* used, not even as a fallback.
* OpenTK/OpenAL — playback-centric, capture support is weaker and OpenAL Soft would also need to be shipped.

## Verification performed (2026-09-09)

* NuGet package `PortAudioSharp2` 1.0.6 depends on the runtime packages
  `org.k2fsa.portaudio.runtime.{win-x64,linux-x64,linux-aarch64,osx-x64,osx-arm64}` 1.0.6.
* Each runtime package was downloaded and inspected: it contains exactly one native library under
  `runtimes/<rid>/native/`:
  * `runtimes/win-x64/native/portaudio.dll`
  * `runtimes/linux-x64/native/libportaudio.so` (imports `libasound.so.2`, `libjack.so.0`, `libc`, `libm`, `libpthread`)
  * `runtimes/osx-arm64/native/libportaudio.dylib` and `runtimes/osx-x64/native/libportaudio.dylib`
    (link only Apple system frameworks)
* The managed API surface (`PortAudio.Initialize/Terminate/DeviceCount/GetDeviceInfo/DefaultInputDevice/
  DefaultOutputDevice`, `Stream` with a `Callback` delegate, `StreamParameters`, `SampleFormat.Int16`) was
  dumped from the compiled assembly by reflection and the wrapper in `Aqorin.Phone.Audio` targets exactly
  that surface. Both input (capture) and output (playback) streams are opened through `Pa_OpenStream`, so
  the same code path works on all three platforms.
* The binding uses `DllImport("portaudio")`; the .NET runtime probes `runtimes/<rid>/native/` in the build
  output and the application directory after a RID-specific publish. On this build machine (Windows 11 x64)
  the smoke test `PortAudioBackendSmokeTests` initialised PortAudio 19.7 and enumerated devices.
* Linux and macOS were **not** executed here; they are exercised by the GitHub Actions matrix
  (build + unit tests incl. the PortAudio smoke test on `ubuntu-latest` and `macos-latest`, and publish
  verification for all four RIDs).

## How native binaries are packaged

* `dotnet build` (portable): the native libraries for all RIDs are copied to
  `bin/<cfg>/net10.0/runtimes/<rid>/native/`. The runtime picks the right one at start-up.
* `dotnet publish -r <rid>`: only the matching native library is copied, flattened next to the executable
  (`portaudio.dll`, `libportaudio.so` or `libportaudio.dylib`). `publish.ps1` / `publish.sh` and the CI
  workflow assert that the file is present.
* Nothing has to be installed on Windows. On Linux the system must provide ALSA (`libasound2`) and the JACK
  client library (`libjack0`, pulled in as a load-time dependency of the bundled build). PulseAudio and
  PipeWire expose ALSA-compatible devices through their ALSA plugins, so they work without extra configuration.
* macOS: the dylib is not code-signed. When the app is started from a Terminal that is allowed to use the
  microphone it works as-is; Gatekeeper may quarantine downloaded binaries
  (`xattr -dr com.apple.quarantine <folder>`). A signed `.app` bundle with `NSMicrophoneUsageDescription` in
  its `Info.plist` is required for App-Store-style distribution and is out of scope for this MVP.

## Sample-rate handling

G.711 runs at 8 kHz. Many consumer devices (especially on macOS) refuse to open at 8 kHz, so
`PortAudioStreamBase` tries the requested rate first and then falls back to the device default (48 kHz /
44.1 kHz / 16 kHz). `LinearResampler` converts between the device rate and 8 kHz (block averaging for integer
down-sampling, linear interpolation otherwise), `FrameAccumulator` re-blocks capture into exact 20 ms frames
and `PcmRingBuffer` bounds playback latency at 400 ms (oldest audio is dropped, starvation is padded with
silence).

## Application-owned interface

The UI, view models and SIP code only see `IAudioDeviceService`, `IAudioCaptureStream` and
`IAudioPlaybackStream` (`Aqorin.Phone.Core`). `Aqorin.Phone.Sip` adapts those to SIPSorcery's
`IAudioSource`/`IAudioSink` (`AppAudioSource`, `AppAudioSink`). If the native library cannot be loaded the
device service still resolves but reports `Backend.IsAvailable == false` with a reason that the UI displays;
there is no silent substitution of another backend.
