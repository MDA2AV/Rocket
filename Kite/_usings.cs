global using System;
global using System.Buffers;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.IO.Pipelines;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using System.Threading;
global using System.Threading.Channels;
global using System.Threading.Tasks;
global using Spring;                 // reuse Spring's io_uring Ring
global using static Spring.Native;   // reuse Spring's io_uring + libc bindings/constants
