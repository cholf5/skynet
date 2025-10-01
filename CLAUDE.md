# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Development Commands

### Build and Test
```bash
# Build entire solution
dotnet build

# Run all tests
dotnet test

# Run specific test project
dotnet test tests/Skynet.Core.Tests/Skynet.Core.Tests.csproj
dotnet test tests/Skynet.Cluster.Tests/Skynet.Cluster.Tests.csproj
dotnet test tests/Skynet.Net.Tests/Skynet.Net.Tests.csproj
dotnet test tests/Skynet.Extras.Tests/Skynet.Extras.Tests.csproj

# Run examples
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj

# Run examples with specific modes
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --cluster node1
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --gate
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --debug-console
```

### Packaging
```bash
# Create NuGet packages
dotnet pack Skynet.sln --configuration Release

# Verify packages locally
./scripts/verify-packages.sh

# Publish to NuGet (requires NUGET_API_KEY)
./scripts/publish-packages.sh
```

## Architecture Overview

Skynet is an Actor framework for .NET 9.0 inspired by cloudwu/skynet, focused on game backend development.

### Core Components

**Skynet.Core** - Core Actor runtime
- `ActorSystem`: Central coordinator for actor lifecycle, routing, and registry
- `ActorRef`: Strongly-typed reference to actors with Send/CallAsync semantics
- `ActorHost`: Internal actor implementation with mailbox-based message processing
- `InProcTransport`: In-process message transport (can be configured for queue simulation)
- Uses `System.Threading.Channels` for mailboxes ensuring message ordering

**Skynet.Cluster** - Cross-node communication
- `IClusterRegistry`: Service discovery interface
- `RedisClusterRegistry`: Redis-backed service registry with TTL-based node health
- `StaticClusterRegistry`: Configuration-based service mapping
- `TcpTransport`: Length-prefixed frame TCP transport with handshake and heartbeat

**Skynet.Net** - Network layer and client connections
- `GateServer`: Unified TCP/WebSocket gateway for external clients
- `SessionActor`: Per-connection actor with message routing and lifecycle management
- Support for custom `ISessionMessageRouter` implementations

**Skynet.Extras** - Utility services
- `DebugConsole`: Telnet-based debugging interface (list/info/trace/kill commands)
- `ActorMetricsCollector`: Runtime metrics collection
- Room management and multicast capabilities

### Key Patterns

**Actor Model**: Single-threaded message processing per actor, no shared state access
- Send: fire-and-forget messaging
- CallAsync: request-response with automatic correlation
- Exception isolation: actor errors don't crash other actors

**Source Generation**: `[SkynetActor]` attribute generates proxies and MessagePack resolvers
- Interface-based actor contracts
- Automatic serialization/deserialization
- Compile-time type safety

**Transport Abstraction**: Local and remote messaging are transparent
- `ITransport` interface allows pluggable transports
- InProc for local development, TCP for distributed systems

### Development Guidelines

- C# 13 (.NET 9.0), Allman braces, Tab indentation
- Async methods return `Task`/`Task<T>`, never `async void`
- Use MessagePack for serialization, no reflection-based serialization
- Microsoft.Extensions.Logging for structured logging
- Unit tests with xUnit + FluentAssertions, >80% coverage for core modules

### Testing Focus Areas

Critical test coverage for:
- Message ordering guarantees
- Send vs CallAsync semantics
- Actor isolation and exception handling
- Service registration and lookup
- Cross-node transparency
- Network connection lifecycle