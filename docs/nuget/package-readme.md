# Skynet Actor Framework

Skynet is a C# actor runtime inspired by cloudwu/skynet. The NuGet packages published from this repository contain the modular building blocks that power the framework:

- **Skynet.Core** – base actor runtime primitives, registry, and in-process transport.
- **Skynet.Cluster** – TCP transport, registry abstractions, and cluster-aware helpers.
- **Skynet.Net** – gateway/session pipeline for TCP and WebSocket clients.
- **Skynet.Extras** – optional rooms, debug console, and supporting utilities.
- **Skynet.Generators** – source generator that wires interface-based RPC contracts.

## Getting Started

1. Install the packages you need, for example:
   ```bash
   dotnet add package Skynet.Core
   dotnet add package Skynet.Cluster
   dotnet add package Skynet.Net
   ```
2. Configure an `ActorSystem` during application start-up, register actors, and start your chosen transport (in-process or TCP).
3. Use generated proxies or the provided actors to send and receive messages, or host the gate server to accept external connections.

See the main README and the documentation under `docs/` for full walkthroughs, deployment examples, and advanced configuration guides.

## Links

- GitHub: https://github.com/openai/skynet
- Documentation: https://github.com/openai/skynet/tree/main/docs
- License: MIT
