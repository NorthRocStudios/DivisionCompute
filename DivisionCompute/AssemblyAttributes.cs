//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using System.Runtime.CompilerServices;

// The console harness compares the symbol-based layouts baked by the source
// generator with the reflection-based layouts the runtime builds, which needs
// access to the internal layout types.
[assembly: InternalsVisibleTo("TranslatorConsole")]
