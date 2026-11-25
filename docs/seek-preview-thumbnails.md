# Seek-bar preview thumbnails ("storyboards")

**Status:** planned, not implemented
**Touches:** `link-cloud/backend` (generate + serve) and `LinkCloudPlayer` (download + show)

Hovering the player's seek bar should show a small frame from that point in
the video, like YouTube/Netflix, instead of just the time.

---

## 1. Why the desktop app can't generate these by itself

The app only reaches videos as HTTP streams. Every jump to a new position is
a new range request, and the server currently takes **~1–4 s to return the
first bytes** of each one (measured from `vlc.log`).

| Client-side option | Why it doesn't work |
|---|---|
| Grab a frame live on hover | 1–4 s per hover position, so the preview lags far behind the cursor |
| Pre-grab a frame every 10 s in the background | ~900 seeks per 2.5 h film: 30–60 min and roughly the **whole file (~3 GB) downloaded again** |
| Pre-grab a frame every 60 s | Still 5–10 min and ~500 MB per film, and too coarse to be useful |

Also note a libvlc 3 constraint found while building library thumbnails:
after `Media.Parse()` on a network URL, playing a **new** `Media` for the same
URL crashes libvlc natively (see the comment in
`LinkCloudPlayer/Services/MediaInfoService.cs`). Any client-side frame
grabbing must reuse the parsed `Media` object.

**The server has the files on local disk**, so it can do the same job in about
a minute per film with zero network transfer. That's what YouTube does too:
previews are prepared once at upload time and served as a few small images.

---

## 2. Design overview

```
download finishes ──► files row inserted ──► enqueue "storyboard" job
                                                   │
                                     ffmpeg (low priority, keyframes only)
                                                   │
                       storage/storyboards/<stored_name>/index.json + sb_000.jpg …
                                                   │
player opens file ──► GET /api/files/:id/storyboard        (manifest JSON)
                  ──► GET /api/files/:id/storyboard/0.jpg  (sprite sheets)
hover seek bar ──► pick tile for time t ──► show it above the time tooltip
```

### Storyboard format

- One frame every **10 s** (every **2 s** for videos under 5 min), scaled into a
  **160×90** tile (letterboxed to keep aspect ratio).
- Tiles packed **10×10 per JPEG sheet** (100 frames ≈ 16 min of video per sheet).
- A 2.5 h film = 900 frames = 9 sheets ≈ **1–2 MB total**.

Manifest (`index.json`):

```json
{
  "version": 1,
  "intervalSec": 10,
  "tileWidth": 160,
  "tileHeight": 90,
  "columns": 10,
  "rows": 10,
  "count": 900,
  "durationSec": 8586.4,
  "sheets": ["sb_000.jpg", "sb_001.jpg", "…"]
}
```

Tile lookup for time `t` (seconds):

```
index = clamp(floor(t / intervalSec), 0, count - 1)
sheet = floor(index / (columns * rows))
cell  = index % (columns * rows)
x     = (cell % columns) * tileWidth
y     = floor(cell / columns) * tileHeight
```

---

## 3. Backend (`link-cloud/backend`)

### 3.1 Prerequisite
`ffmpeg` + `ffprobe` on the server (`sudo apt install ffmpeg`). The backend
doesn't use ffmpeg anywhere yet. Add a startup check that logs a warning (and
disables the feature) if it's missing.

### 3.2 Generation job
- **Separate BullMQ queue** (e.g. `storyboards`), not the download queue, so a
  slow ffmpeg run never delays downloads. Concurrency 1.
- **Enqueue** right after the `INSERT INTO files` in
  `src/services/downloadService.js` (~line 434), only for `video/*` mime types
  (use `utils/mimeGuess.js`). Job data: `{ fileId, storedName }`.
- **Worker** (e.g. `src/workers/storyboardWorker.js`, or a second `Worker` in
  the existing worker process):
  1. `ffprobe` the file for its duration, then pick `intervalSec`.
  2. Write to a temp dir: `storage/storyboards/<stored_name>.tmp/`.
  3. Run ffmpeg (keyframe-only decode keeps this fast, about 1–2 min per film):
     ```bash
     nice -n 19 ffmpeg -hide_banner -loglevel error \
       -skip_frame nokey -i "<file>" -an -sn \
       -vf "fps=1/<intervalSec>,scale=160:90:force_original_aspect_ratio=decrease,pad=160:90:(ow-iw)/2:(oh-ih)/2,tile=10x10" \
       -fps_mode vfr -q:v 5 "<tmp>/sb_%03d.jpg"
     ```
  4. Count the frames (`count = ceil(duration / intervalSec)`), write `index.json`,
     then **atomically rename** the temp dir to `storage/storyboards/<stored_name>/`.
  5. On failure: delete the temp dir and log it. Playback is unaffected (the
     feature is optional).
- Paths must go through `storageService` helpers (same `path.basename` /
  root check as `filePath()`) so a crafted `stored_name` can't escape the
  storage directory.

### 3.3 Endpoints (`src/routes/files.js`, `src/controllers/fileController.js`)
| Route | Returns |
|---|---|
| `GET /api/files/:id/storyboard` | `index.json`, or `404` if none (yet) |
| `GET /api/files/:id/storyboard/:sheet.jpg` | One sheet image. Validate `sheet` is a number and in range; never pass user input into a path. |

- Auth: `requireAuth` (cookie), with ownership via the existing `findOwned()`.
  The desktop app fetches these with its own `HttpClient`, which already has
  the cookie, so the `?token=` variant isn't needed here.
- Headers: `Cache-Control: private, max-age=86400` (storyboards never change
  for a given file).

### 3.4 Cleanup and backfill
- `exports.remove` (delete file): also delete `storage/storyboards/<stored_name>/`.
- One-off script `npm run storyboards:backfill`: enqueue every existing video
  file that has no storyboard dir. With concurrency 1 it works through the
  library in the background.
- Optional: add `storyboard_ready TINYINT` to `files` (new migration in
  `src/db/migrations/`) and include it in `GET /api/files`, so clients know
  without an extra request. Without it, the client just tries the manifest and
  treats `404` as "none".

---

## 4. Desktop app (`LinkCloudPlayer`)

### 4.1 Fetching
- New `Services/StoryboardService.cs`, using `ApiClient`'s `HttpClient` (cookie auth):
  - `GetManifestAsync(fileId)` returns the manifest, or null on 404.
  - `GetSheetAsync(fileId, n)` returns a frozen `BitmapSource`.
- Cache to `%LocalAppData%\LinkCloudPlayer\storyboards\<fileId>_<sizeBytes>\`
  (same keying as the thumbnail cache) so re-opening a film costs nothing.
- When the player opens, load the manifest in the background, then sheets
  **lazily**: the sheet under the cursor first, the rest after. Never block
  playback on this.

### 4.2 Showing
In `Views/PlayerControlsWindow.xaml`, extend the existing `SeekTooltip`
(currently a time label) into a card:

```
┌────────────────┐
│   160×90 tile  │   ← Image with Source = CroppedBitmap(sheet, tileRect)
├────────────────┤
│    1:12:04     │   ← existing SeekTooltipText
└────────────────┘
```

- In `ShowSeekHover(fraction)`: compute `t`, then the tile rect (formula in §2),
  set the image. Cache `CroppedBitmap`s per index (cheap) to avoid allocating
  one on every mouse move.
- Keep the card clamped inside the seek bar (the current tooltip clamping logic).
- Show it while scrubbing (dragging) too; that's when it's most useful.
- No manifest, or the sheet isn't loaded yet: fall back to today's time-only tooltip.

### 4.3 Edge cases
- Files added before the backend change: no storyboard until the backfill runs.
- Audio files: no storyboard (backend skips non-video).
- Very short videos: the 2 s interval keeps the previews useful.
- Mismatch between the manifest `durationSec` and libvlc's length: always
  clamp the index.

---

## 5. Effort estimate

| Part | Size |
|---|---|
| Backend queue + worker + ffmpeg | ~150 lines |
| Endpoints + cleanup + backfill script | ~80 lines |
| Desktop service + tooltip UI | ~150 lines |
| Server setup | install ffmpeg, deploy, run the backfill once |

The website's player (`frontend/src/components/VideoPlayer.jsx`) could reuse
the same endpoints later.
