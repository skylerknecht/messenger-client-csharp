# C# Messenger Client

## Overview

Compiles to a native Windows .NET Framework 4.7.2 executable with no additional dependencies.

## Capabilities

| Capability             | Status                                             |
|------------------------|----------------------------------------------------|
| Transports             | HTTP and WebSockets                                |
| Encryption             | AES-256-CBC with random IV prefix                  |
| Reconnection           | 5 attempts over 60 seconds (configurable)          |
| SOCKS5 TCP             | Supported                                          |
| SOCKS5 UDP             | Not Supported                                      |
| Remote Port Forwards   | Supported (server-initiated via `remote` command)  |

## Quick Start

```
operator~# python builder.py -e test
[+] Wrote C# client to 'ServiceClient'

operator~# cd ServiceClient/ServiceClient
operator~# dotnet build -c Release
target> bin\Release\net472\ServiceClient.exe
[+] Connected to ws://localhost:8080/
```

The output is a .NET Framework 4.7.2 project that can be compiled with `dotnet build` on any platform.

## Builder Options

Run `builder.py` directly or use `messenger-builder csharp` from the [Messenger repository](https://github.com/skylerknecht/messenger).

Options provided to the builder are hardcoded into the source files. The operator can override them at runtime with the same flags.

### Builder-Only Options

| Flag          | Default        | Description                                    |
|---------------|----------------|------------------------------------------------|
| `--name`          | ServiceClient  | Output directory name                          |
| `--no-print`      | off            | Suppress all stdout/stderr at startup          |
| `--exit-on-close` | off            | Terminate the host process on kill signal       |

### Client Configuration

| Flag                    | Default        | Description                              |
|-------------------------|----------------|------------------------------------------|
| `--server-url`          | localhost:8080 | Server URL (protocol sets transport)     |
| `-e`, `--encryption-key`| (none)        | AES encryption key                       |
| `--user-agent`          | Chrome 141     | HTTP/WebSocket User-Agent string         |
| `--proxy`               | (none)         | HTTP proxy (`http://user:pass@host:port`)|

### Retry Behavior

| Flag                | Default | Description                           |
|---------------------|---------|---------------------------------------|
| `--retry-duration`  | 60      | Total seconds to keep retrying        |
| `--retry-attempts`  | 5       | Number of reconnection attempts       |

Set `--retry-attempts 0` to disable reconnection.

## Transport Selection

The protocol in `--server-url` determines the transport:

- `http://` or `https://` — HTTP polling
- `ws://` or `wss://` — WebSocket

## Remote Port Forwards

Remote port forwards are configured server-side with the `remote` command, not at build time. See the [operator guide](https://github.com/skylerknecht/messenger/blob/main/docs/remote-port-forwards.md).
