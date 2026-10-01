// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Abstractions.Networking.Packets;

/// <summary>
/// Specifies that the annotated packet handler method bypasses the middleware pipeline,
/// executing the terminal handler directly to achieve minimal dispatch latency.
/// </summary>
/// <remarks>
/// <para>
/// Handlers marked with this attribute skip all inbound, outbound, and outbound-always
/// middleware registered in <c>PacketDispatchOptions.WithMiddleware</c> (e.g., concurrency,
/// rate limiting, timeout).
/// </para>
/// <para>
/// Core security checks (permission level and required encryption) defined directly on the
/// handler method remain enforced by the dispatcher.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class BypassMiddlewareAttribute : Attribute
{
}
