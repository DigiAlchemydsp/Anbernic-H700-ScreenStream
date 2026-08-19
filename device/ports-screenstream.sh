#!/bin/sh
# ScreenStream Ports entry (Knulli/Batocera/ArkOS/AmberELEC/ROCKNIX) - fb0
# H.264 tcp:5555. First launch starts the stream; launching again stops it.
# Read-only, no tunings.
VPIDF=/var/run/screenstream.pid
VENG=""
for c in "$(dirname "$0")/stream.sh" /userdata/screenstream/stream.sh \
         /storage/screenstream/stream.sh /roms2/screenstream/stream.sh \
         /roms/screenstream/stream.sh; do
  [ -f "$c" ] && VENG="$c" && break
done
if [ -z "$VENG" ]; then
  echo "stream.sh not found - run the FatmaVision Setup Wizard first"
  sleep 4
  exit 1
fi

echo "===== SCREENSTREAM ====="
echo "video: fb0 -> H.264 tcp:5555"

if [ -f "$VPIDF" ] && kill -0 "$(cat "$VPIDF")" 2>/dev/null; then
    echo "stream RUNNING -> stopping"
    kill -TERM -"$(cat "$VPIDF")" 2>/dev/null
    rm -f "$VPIDF"
    sleep 2
    echo "stopped."
else
    echo "starting video stream on port 5555 ..."
    setsid nohup sh "$VENG" 5555 </dev/null >/tmp/screenstream.log 2>&1 &
    echo $! > "$VPIDF"
    sleep 3
    if [ -f "$VPIDF" ] && kill -0 "$(cat "$VPIDF")" 2>/dev/null; then
        echo "stream UP -> connect with the FatmaVision app"
    else
        echo "FAILED - see /tmp/screenstream.log"
    fi
fi
echo "returning to the frontend in 4s ..."
sleep 4
exit 0
