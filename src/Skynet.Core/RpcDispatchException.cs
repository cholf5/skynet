using System;

namespace Skynet.Core;

public sealed class RpcDispatchException : InvalidOperationException
{
	public RpcDispatchException(string message)
	: base(message)
	{
	}
	}
