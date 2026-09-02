# FlashAirMirror

Mirrors a local directory to a FlashAir SD card over HTTP.
I created this tool to syncronise assets for my Sega Dreamcast projects at build time.

## Usage

```
flashair-mirror <ip> <source-path> <dest-root> [timeout-seconds]
```

## Arguments

- `<ip>`: IP address of the FlashAir device
- `<source-path>`: Local directory to mirror
- `<dest-root>`: Destination path on FlashAir (e.g., `MyGame`)
- `<timeout-seconds>`: Connection timeout in seconds

