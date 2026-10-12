using System.Runtime.InteropServices;

namespace InternetMonitor.Ui;

/// <summary>
/// P/Invoke declarations used by the UI layer only.
///
/// Deliberately the classic <see cref="DllImportAttribute"/> rather than the source-generated
/// LibraryImport: that generator emits unsafe code and so requires AllowUnsafeBlocks for the whole
/// project. Turning that on across the codebase to marshal one <see cref="IntPtr"/> is a poor
/// trade - there is no marshalling here for a generator to optimise.
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr handle);
}
