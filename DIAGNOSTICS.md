# Diagnostic build 1.0.2.1

This custom build adds bounded internal diagnostics and recovery for unexpected virtual display loss. The original disconnection cause remains unconfirmed. It is based on upstream commit a827c7137659b618d0a65f261ad8b2da1c74f772.

- Keep driver heartbeats independent of display management and log original Windows I/O errors.
- Reopen the driver handle after three consecutive heartbeat failures, at most once per 30 seconds. Protect handle lifetime while allowing heartbeat and display-add operations to overlap.
- Check missing displays every five seconds after a startup grace period. Require two missing samples; throttle recovery and respect manual removal and suspend.
- Write asynchronous logs with timestamps and minute heartbeat summaries. Queue limit: 64 messages; message limit: 2,000 characters. Prune every minute to retain the last hour, capped at 512 KiB after pruning.

Logs default to `logs/debug.log` beside the executable. Set `PARSEC_DISPLAY_LOG_DIR` to override the directory. No external monitoring script is required. A separate console window is not required.

## Build

Use Windows, a current .NET SDK (validated with 10.0.404), and .NET Framework 4.7.2 or later for running the application and test executable. Restore requires NuGet access.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-diagnostics.ps1
```

Output is in `artifacts/app`. Keep the executable, config and dependency DLLs together. The existing Parsec virtual display driver must already be installed. Stop the previous display manager before starting this executable. The ZIP contains the manager, not a driver installer.

## Validation

Mock-driver tests cover heartbeat failure thresholds, retry throttling, missing display policy, manual removal/suspend, concurrent I/O and handle lifetime, log retention/UTF-8 limits and asynchronous flush. Release build passed with zero warnings/errors. A short deployment observation showed one 1920x1080@30 virtual display and no heartbeat failures for approximately nine minutes. No real display failure was deliberately induced; long-term stability has not yet been established.

The locally validated executable SHA-256 is `5E3D9C76172032B94E3822645D5EC1A848C3B7A1008C8204A27CC5C7CDDC9F11`. Rebuild hashes can differ.
