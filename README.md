# FatmaVision

Screen streaming for Linux handhelds: watch the device screen on your
Windows PC over wifi with low latency. The app sets up and manages the
device side over SSH, so you never touch the handheld.

This repo holds the source code. Download the ready-to-run exe from the
[release repo](https://github.com/DigiAlchemydsp/FatmaVision-Release).

## What it does

- Encodes the handheld framebuffer (`/dev/fb0`) to H.264 on the device and
  serves it over plain TCP on port 5555
- Connects from Windows, decodes with the built-in H264 decoder MFT and
  renders 640x480 at 30 fps
- Deploys and manages the device side over SSH: probe, install, start and
  stop ("set and forget")
- One-click recording to playable `.mkv` files

## How it works

The handheld runs `device/stream.sh`: `ffmpeg` reads `/dev/fb0`, encodes
H.264 Baseline (ultrafast, zerolatency, GOP 12, no B-frames) and serves a
raw Annex-B elementary stream on `tcp://0.0.0.0:5555`. The Windows client
demuxes the byte stream into access units and decodes them.

The protocol is deliberately simple: no handshake, no framing, no audio,
one client at a time; the device re-listens automatically after every
disconnect. Any client that speaks raw Annex-B over TCP can display the
stream.

## Supported devices

Any Linux handheld whose frontend renders to a framebuffer, with SSH
enabled and `ffmpeg` (with an H.264 encoder) in the image. Tested:

- **Knulli / Batocera** (H700, RK3326, RK3399, x86_64) - SSH key auth out of the box
- **ArkOS** - SSH user `ark`, password `ark`
- **AmberELEC / ROCKNIX** - root + key auth
- **muOS** - SSH user `root`, password `muos` (enable SSH in the muOS settings first)
- Anything else with `/dev/fb0` + `ffmpeg` is detected automatically and set
  up under the first writable storage area (`/userdata`, `/storage`,
  `/roms2`, `/roms`, `/mnt/mmc/MUOS`)

The wizard probes the device over SSH and adapts the install location and
Ports launcher to the detected OS. A device is accepted when it has a
framebuffer (`/dev/fb0`), `ffmpeg` with an H.264 encoder and a writable
storage area.

## Getting started

1. Get `FatmaVision.exe` from the
   [release repo](https://github.com/DigiAlchemydsp/FatmaVision-Release).
2. Run it - no installation, no external files (icons and device scripts
   are baked into the exe).
3. First time: click **Set Up Device**, enter your handheld's IP (device
   OS detection is automatic), then **Set Up Device & Start Stream** - it
   connects over SSH, installs the stream on the device and starts it.
   Press **Done**.
4. Click **Connect** - the device screen appears. From then on, Connect
   also starts the device stream automatically and **Disconnect** stops
   it; you never touch the device.
5. Optional: **Start Recording** saves playable `.mkv` files to
   `Captures\` next to the exe. Press **F** for a fullscreen view.

Requirements: Windows 10/11; SSH key auth to the device (default
Knulli/Batocera setup) or PuTTY `plink.exe` on PATH with a password (needed
for ArkOS/muOS password auth).

## Build from source

```
powershell -File build.ps1
```

Uses the .NET Framework compiler that ships with Windows. The build embeds
`FatmaVision.ico`; `device/` holds the scripts the wizard deploys to the
handheld. Keep the `src/DeviceSetup.cs` base64 constants in sync with
`device/*.sh` (the md5 of the decoded constants must equal the md5 of the
files).

## Testing

The setup wizard verifies the stream end-to-end on the device (30 decoded
frames) before reporting it ready.

## License

MIT, see `LICENSE`.
