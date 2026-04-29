// Polyfill so C# records compile under netstandard2.0 (which doesn't ship IsExternalInit).
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
