# Architecture notes

## Threading: never block SIPSorcery's transport thread

`SIPTransport` hands every inbound SIP message to **one** long-running thread (`ProcessReceiveQueue`) and waits
(`.Wait()`) for the handler chain to finish before taking the next message. Everything that happens in response to
a SIP message — `SIPUserAgent` answering a 200 OK, processing a BYE, the registration agent handling a 401/200 —
therefore runs on that thread. If any handler blocks, **the softphone goes deaf**: registration refreshes "do not
respond", INVITE responses are never processed and the next call "times out", although the network is fine.

Two of our code paths used to do exactly that:

* `MediaSession.Start()` (after 200 OK) → `IAudioSource.StartAudio()` → PortAudio `Pa_OpenStream`/`Pa_StartStream`.
* `MediaSession.Close()` (after BYE) → `IAudioSource.CloseAudio()` → PortAudio `Pa_StopStream`/`Pa_CloseStream`.

Native audio calls can take hundreds of milliseconds and, with a misbehaving driver or a callback that is itself
blocked inside the RTP stack, can hang indefinitely.

Rules now enforced in `Aqorin.Phone.Sip.Media`:

1. **Device open/stop/close never runs on the caller's thread.** `AppAudioSource`/`AppAudioSink` offload to the
   thread pool and wait at most `OpenTimeout` (10 s) / `CloseTimeout` (5 s). A hung device is logged and abandoned;
   the call continues (one-way) or ends normally.
2. **The native audio callback only encodes and enqueues.** A dedicated pacing loop (`PeriodicTimer`, 20 ms) raises
   `OnAudioSourceEncodedSample`; silence is sent when the microphone has not delivered a frame. PortAudio's callback
   thread never enters SIPSorcery, so `Pa_StopStream` can never wait on the RTP stack.
3. **No lock shared between the callback and stop/close.** Formats are swapped via `Volatile` reads of an immutable box.
4. `SipCallService` closes media sessions on the thread pool, and `SipSorceryAudioMediaSession.CloseAsync` is
   non-blocking.
5. `SipRegistrationService` re-opens the SIP transport (new socket) before a registration retry when no call is in
   progress, so a socket that stopped receiving is replaced rather than retried forever.
6. Out-of-dialog `OPTIONS` requests are answered with `200 OK` (reachability probes).

Regression coverage: `tests/Aqorin.Phone.Sip.Tests/Loopback/HangingAudioDeviceTests.cs` (audio device that hangs
on close; hang-up and the next call must still complete within seconds) and `ConsecutiveCallTests.cs`.

## Password storage

`SipAccountSettings` never contains the password. `JsonSettingsStore` stores it separately, base64 of ciphertext,
only when *Remember password* is on:

* Windows: DPAPI (`ProtectedData`, `CurrentUser` scope, app-specific entropy).
* macOS/Linux: AES-256-GCM with a random 256-bit key in `<config>/Aqorin.Phone/.credential-key` (mode 0600).
  This keeps the password out of plaintext files and backups; it does **not** defend against another process running
  as the same user. Keychain/libsecret integration is a possible follow-up.

The ciphertext is tagged with the scheme (`dpapi-user` / `aes-gcm-userkey`); a file copied to another OS or user
simply requires re-entering the password.
