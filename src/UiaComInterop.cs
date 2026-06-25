using System.Runtime.InteropServices;

namespace MsTeamsLocal;

// Minimal COM interop for the UI Automation client (UIAutomationCore.dll), declared by hand
// because `dotnet build` (.NET Core MSBuild) cannot resolve a tlbimp COMReference. Only the
// surface needed to actuate a control via the focus-free MSAA default action is declared:
// locate the element by AutomationId, then call its LegacyIAccessible DoDefaultAction. Unlike
// the managed InvokePattern.Invoke() on Teams' Chromium controls, that action neither takes
// focus nor foregrounds Teams.
//
// COM vtable layout is positional, so every method up to the ones we call must be declared in
// exact order; unused slots are explicit `_unusedNN` placeholders (one slot each). Conditions
// are passed through as opaque IUnknown to avoid a QueryInterface for a condition interface.

internal static class UiaTreeScope
{
    public const int Descendants = 4;
}

[ComImport]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")] // IID_IUIAutomation
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    void _unused01();  // CompareElements
    void _unused02();  // CompareRuntimeIds
    IUIAutomationElement GetRootElement();               // slot 3
    IUIAutomationElement ElementFromHandle(IntPtr hwnd); // slot 4
    void _unused05();  // ElementFromPoint
    void _unused06();  // GetFocusedElement
    void _unused07();  // GetRootElementBuildCache
    void _unused08();  // ElementFromHandleBuildCache
    void _unused09();  // ElementFromPointBuildCache
    void _unused10();  // GetFocusedElementBuildCache
    void _unused11();  // CreateTreeWalker
    void _unused12();  // get_ControlViewWalker
    void _unused13();  // get_ContentViewWalker
    void _unused14();  // get_RawViewWalker
    void _unused15();  // get_RawViewCondition
    void _unused16();  // get_ControlViewCondition
    void _unused17();  // get_ContentViewCondition
    void _unused18();  // CreateCacheRequest
    void _unused19();  // CreateTrueCondition
    void _unused20();  // CreateFalseCondition
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object CreatePropertyCondition(                       // slot 21
        int propertyId,
        [MarshalAs(UnmanagedType.Struct)] object value);
}

[ComImport]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")] // IID_IUIAutomationElement
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void _unused01();  // SetFocus
    void _unused02();  // GetRuntimeId
    IUIAutomationElement? FindFirst(int scope,
        [MarshalAs(UnmanagedType.IUnknown)] object condition);                   // slot 3
    void _unused04();  // FindAll
    void _unused05();  // FindFirstBuildCache
    void _unused06();  // FindAllBuildCache
    void _unused07();  // BuildUpdatedCache
    void _unused08();  // GetCurrentPropertyValue
    void _unused09();  // GetCurrentPropertyValueEx
    void _unused10();  // GetCachedPropertyValue
    void _unused11();  // GetCachedPropertyValueEx
    void _unused12();  // GetCurrentPatternAs
    void _unused13();  // GetCachedPatternAs
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object? GetCurrentPattern(int patternId);            // slot 14
}

[ComImport]
[Guid("828055ad-355b-4435-86d5-3b51c14a9b1b")] // IID_IUIAutomationLegacyIAccessiblePattern
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationLegacyIAccessiblePattern
{
    void _unused01();        // Select
    void DoDefaultAction();  // slot 2
}
