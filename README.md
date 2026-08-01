# C# Messenger Client

## Overview

The Client is a .NET Framework 4.7.2 Messenger Client targeting Windows environments. The framework is preinstalled on Windows 10/11 and most enterprise systems, requiring no additional runtime to deploy.

## Primary Capabilities

| Capability                 | Support Status                                         |
|----------------------------|--------------------------------------------------------|
| Transports                 | HTTP and WebSockets                                    |
| Encryption                 | AES-256-CBC with random IV prefix.                     |
| Reconnection procedure     | Defaults to five (5) attempts over sixty (60) seconds. |
| SOCKS5 TCP                 | Supported                                              |
| SOCKS5 UDP                 | Not Supported                                          |

## Client-Specific Capabilities

| Capability                    | Support Status                                                                                         |
|-------------------------------|--------------------------------------------------------------------------------------------------------|
| Compilation                   | Outputs a project directory requiring compilation with `msbuild` (Windows) or Mono's `msbuild` (Linux). |

## Quick Start

```
operator~# python builder.py --encryption-key test
[+] Wrote C# client to 'MessengerClient'

operator~# cd MessengerClient
operator~# msbuild MessengerClient.sln /p:Configuration=Release
operator~# bin\Release\MessengerClient.exe
```

## Usage

To build the client, execute `builder.py` or `messenger-builder` from the [Messenger Repository](https://github.com/skylerknecht/messenger).

Both scripts accept the same options and will generate a C# Messenger Client project directory. If provided options, the builder scripts
will hard-code the options into the source files. The output is a complete .NET Framework 4.7.2 project that can be compiled with
`msbuild` (Windows) or Mono's `msbuild` (Linux). Those options and their definitions are shown below.

## Client Options

| Option                                        | Flag                      | Default Value          |
|-----------------------------------------------|---------------------------|------------------------|
| [Server URL](#server-url)                     | `--server-url`            | localhost:8080         |
| [Encryption Key](#encryption-key)             | `--encryption-key`        | None                   |
| [User Agent](#user-agent)                     | `--user-agent`            | [Specified Here](https://github.com/skylerknecht/messenger-client-csharp/blob/main/builder.py#L7) |
| [Proxy](#proxy)                               | `--proxy`                 | None                   |
| [Remote Port Forwards](#remote-port-forwards) | `--remote-port-forwards`  | None                   |
| [Retry Duration](#retry-duration)             | `--retry-duration`        | One Minute             |
| [Retry Attempts](#retry-attempts)             | `--retry-attempts`        | Five                   |
| [Name](#name)                                 | `--name`                  | MessengerClient        |

### Server URL

Once the Messenger Server is running, the operator will be provided a server URL that can be set with `--server-url`.

```
builder.py --server-url http://localhost:8080
```

The client will attempt to establish a connection to the server based on the protocol specified in the server URL. For HTTP, leave the protocol as
`http://`, for websockets use `ws://`. Given that the server is listening with SSL encryption, provide the SSL
alternative to each protocol.

#### Encryption Key

Messenger Server will also provide an encryption key upon startup that can be hardcoded.

```
builder.py --encryption-key SuP3rs_crEtk3y
```

Since the server expects encryption, the default will likely cause issues; therefore, the client outputs an
error.

#### User Agent

For HTTP-based protocols, the operator can control the user-agent header.

```
builder.py --user-agent "Test User Agent"
```

#### Proxy

Enterprise environments typically have outbound proxies. Operators can provide a proxy using the HTTP-proxy schema.

```
builder.py --proxy http://user:password@localhost:8080
```

#### Remote Port Forwards

Messenger expects clients to attempt to set up remote port forwards on the client side. The operator can specify multiple port forwards
with the schema `LISTENING-HOST:LISTENING-PORT:DESTINATION-HOST:DESTINATION-PORT`.

```
builder.py --remote-port-forwards localhost:8080:remotehost:8080
```

This will forward all local connections on 8080 to a remote host on 8080. Given that the operator has not permitted the connection server-side,
they will see the following message.

```
[!] Messenger `test` has no Remote Port Forwarder configured for remotehost:8080, denying forward!
```

#### Retry Duration

Clients will disconnect for various reasons. Given that the client does not completely exit, it will attempt to reconnect. Operators can
control how long the client will attempt to reconnect by specifying a retry duration. This value is expected to be in seconds. For example,
if the retry duration is set to 120, then the client will attempt to reconnect for two minutes.

```
builder.py --retry-duration 100
```

To disable reconnection attempts, set the retry attempts option to 0.

#### Retry Attempts

In combination with the retry duration, retry attempts determine the minimum time the client waits between reconnection attempts.

```
builder.py --retry-attempts 100
```

#### Name

The build process outputs a project directory, and operators can control its name.

```
builder.py --name CustomClient
```
