# Link Cloud Windows Player — project plan

## Context

The web app's `<video>` player (`frontend/src/components/VideoPlayer.jsx`) is
limited to whatever codecs the browser itself can decode. Files downloaded
through link-cloud can be almost anything (`.mkv`, `.avi`, `.wmv`, etc. — see
`backend/src/utils/mimeGuess.js`), and some of them — most commonly MKVs with
AC3/DTS audio — have no audio decoder in any browser at all. That's not a bug
in the player, it's a hard browser limitation.

The fix: a personal Windows app, built in Visual Studio/C#, that embeds VLC's
actual decoder (via LibVLCSharp) instead of the browser's. Since VLC ships
its own codec libraries, this plays anything, with sound. The goal is a
Plex-style app: launch it, see the library pulled from the existing
link-cloud server, click a file, play it — with a full-featured player
(multi audio track selection, subtitles, all the usual controls).

## Stack

- **.NET 8, WPF**, built in Visual Studio.
- **LibVLCSharp** + **LibVLCSharp.WPF** + **VideoLAN.LibVLC.Windows** (NuGet) — bundles the real libvlc runtime, so nothing extra needs installing on the PC beyond the app itself.
- **HttpClient** (`HttpClientHandler` with `CookieContainer`) to talk to the existing Node/Express backend, which authenticates via an httpOnly cookie-based JWT (`backend/src/middleware/auth.js`, `backend/src/controllers/authController.js`).
- Plain, lightweight code-behind / light MVVM — this is a one-user app, no need for heavier architecture.

## Backend integration

Existing endpoints this app uses as-is, no backend changes:
- `POST /api/auth/login` `{email,password}` → sets the `token` cookie (7-day JWT). The app's `HttpClientHandler` must have `UseCookies = true` with a `CookieContainer` to capture and resend it on every request.
- `GET /api/auth/me` → confirms the session / fetches the current user.
- `GET /api/files` → the library: `{id, original_name, size_bytes, mime_type, source_url, created_at}[]`.

One small, isolated backend change is needed:
- `GET /api/files/:id/stream` (`backend/src/controllers/fileController.js:35`) currently authenticates only via the cookie (`middleware/auth.js`'s `requireAuth`). LibVLC opens media by URL and doesn't share .NET's `CookieContainer` — handing it a cookie-authenticated URL isn't practical. The standard fix (same pattern Plex/Jellyfin use): let this one route *also* accept the JWT as a `?token=` query parameter, falling back to the cookie if absent. Add a small `requireAuthQueryOrCookie` variant in `middleware/auth.js`, used only on the `stream` (and optionally `download`) route — the browser's cookie-based flow is untouched. The desktop app then just builds `https://.../api/files/<id>/stream?token=<jwt>` and hands that straight to LibVLC.

Job/download endpoints (`/api/jobs`) are **not** in scope for v1 — the ask is
"load files from the server and play them," not trigger new downloads from
the desktop app. Could be a later addition.

## Features

1. **Library browser** — grid/list from `GET /api/files`: name, size, date, file-type icon; search box; sort by name/date/size. Mirrors `frontend/src/components/FileList.jsx`'s data, native UI.
2. **Advanced player**, built around `LibVLCSharp.WPF`'s `VideoView`, matching and extending `VideoPlayer.jsx`'s control set:
   - Play/pause, seek bar with buffered range, ±10s skip, volume/mute, fullscreen, playback-speed menu, keyboard shortcuts (space/k, arrows, m, f) — same shape as the existing web player.
   - **Audio track selection**: `MediaPlayer.AudioTrackDescription` lists every audio track (language/codec) in the file; a menu lets the user switch via `MediaPlayer.SetAudioTrack(id)` — this is what makes multi-audio files (dual language, commentary tracks) usable.
   - **Subtitles**:
     - Embedded (soft) subtitle tracks — same pattern as audio, via `MediaPlayer.SpuDescription` / `SetSpu(id)`, plus an "Off" entry.
     - External subtitle files (`.srt`/`.ass`/`.vtt`) — a "Load subtitle..." file picker calling `MediaPlayer.AddSlave(MediaSlaveType.Subtitle, uri, true)`.
3. **Session**: login screen; server URL + credentials saved via Windows DPAPI (`System.Security.Cryptography.ProtectedData`) so the user isn't re-typing a password every launch.
4. **Resilience**: clear error state on stream failure / server unreachable, with a retry action.

## Project structure

```
D:\react\link-cloud-windows\
  PLAN.md
  LinkCloudPlayer.sln
  LinkCloudPlayer\
    App.xaml / App.xaml.cs
    MainWindow.xaml(.cs)              — shell + navigation
    Views/LoginView.xaml(.cs)
    Views/LibraryView.xaml(.cs)       — file grid/list
    Views/PlayerView.xaml(.cs)        — VideoView + custom control overlay
    Services/ApiClient.cs            — Login / GetFiles / BuildStreamUrl
    Services/CredentialStore.cs      — DPAPI-backed saved session
    Models/FileItem.cs, Models/User.cs
    README.md                        — build/run instructions
```

## Build order (for when implementation starts)

1. Scaffold the WPF project + NuGet packages; confirm a hardcoded local file plays in a bare `VideoView` (sanity check LibVLCSharp itself works).
2. Backend: add the `?token=` fallback auth to the stream route.
3. Login screen → `ApiClient.Login` → `GET /api/auth/me` to confirm the session.
4. Library view wired to `GET /api/files`; clicking an item opens the player with its stream URL.
5. Core player controls (play/pause/seek/volume/fullscreen/speed).
6. Audio-track and subtitle-track selection menus.
7. External subtitle file loading.
8. Polish: remembered playback position per file (small local JSON cache), a settings screen for the server URL.

## Verification (once built)

- Log in, confirm the library matches the web dashboard's file list.
- Play an MKV with AC3/DTS audio that has no sound in the browser — confirm it now plays with sound (this is the whole premise of the app).
- Play a file with 2+ audio tracks and confirm the audio menu switches between them live.
- Load an external `.srt` for a file with no embedded subtitles and confirm it renders.
- Kill the network mid-playback and confirm a clean error state rather than a crash.
