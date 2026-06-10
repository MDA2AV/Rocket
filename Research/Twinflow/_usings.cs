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
global using static Twinflow.Native;   // Twinflow's own io_uring + libc bindings (self-contained)
