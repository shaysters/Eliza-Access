// Required for C# 9 'record' types when targeting .NET Framework.
// The compiler emits a reference to this type for init-only setters.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
