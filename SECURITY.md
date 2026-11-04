# Security

## Reporting a vulnerability

Please report security problems privately through GitHub's "Report a vulnerability" button on this repository's
Security tab rather than a public issue. Include the version, how you run CherBot (Windows app, service, Linux) and
the steps to reproduce.

## Trust model

- The management API (`/v1`) and web UI are protected by a bearer token in `{data dir}/api-token`. **Anyone with the
  token can run arbitrary code on the machine**, because actions can contain C# scripts and shell commands. Keep the
  API bound to `127.0.0.1` unless you need remote access, and treat the token like a password.
- The Streamer.bot-compatible WebSocket/HTTP/UDP servers have no authentication unless you set
  `CherBot:WebSocketServer:Password`. Browser requests are limited to loopback pages and `CherBot:AllowedOrigins`.
  Do not expose these ports to untrusted networks without a password.
- Imported feature packs and Streamer.bot exports can contain scripts. Review them before enabling.
