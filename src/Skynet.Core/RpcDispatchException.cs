using System;

namespace Skynet.Core;

public sealed class RpcDispatchException(string message) : InvalidOperationException(message);
