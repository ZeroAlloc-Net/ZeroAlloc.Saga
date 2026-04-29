using System.Runtime.CompilerServices;
using VerifyTests;

namespace ZeroAlloc.Saga.Generator.Tests;

internal static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init() => VerifySourceGenerators.Initialize();
}
