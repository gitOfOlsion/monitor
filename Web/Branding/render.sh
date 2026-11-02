#!/bin/sh
# Regenerates the PNG images in Web/wwwroot from the SVG sources in this folder.
# Run from the repository root:
#   docker run --rm -v "$PWD/Web:/w" alpine:3.20 sh /w/Branding/render.sh
set -eu
apk add --no-cache rsvg-convert font-liberation font-dejavu >/dev/null
cd /w
rsvg-convert -w 1200 -h 630 Branding/og-image.svg -o wwwroot/og-image.png
rsvg-convert -w 512  -h 512 Branding/icon.svg     -o wwwroot/icon-512.png
rsvg-convert -w 192  -h 192 Branding/icon.svg     -o wwwroot/icon-192.png
rsvg-convert -w 180  -h 180 Branding/icon.svg     -o wwwroot/apple-touch-icon.png
rsvg-convert -w 32   -h 32  wwwroot/favicon.svg   -o wwwroot/favicon-32.png
ls -l wwwroot/*.png
