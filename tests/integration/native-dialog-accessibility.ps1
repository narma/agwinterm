# Direct MSAA contract of standard Win32 controls. UIA proxy availability is process-wide.
Add-Type -Path "$PSHOME/Accessibility.dll"
Add-Type -ReferencedAssemblies "$PSHOME/Accessibility.dll" -CompilerOptions '/nowarn:1701' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Accessibility;
public static class NativeDialogAccess {
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr hwnd,uint id,ref Guid iid,[MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);
    static IAccessible Read(IntPtr hwnd) {
        var iid=new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
        int hr=AccessibleObjectFromWindow(hwnd,0xfffffffc,ref iid,out var accessible);
        if(hr<0)Marshal.ThrowExceptionForHR(hr);return accessible;
    }
    public static string Name(IntPtr hwnd) { var a=Read(hwnd);try{return a.get_accName(0);}finally{Marshal.ReleaseComObject(a);} }
    public static string[] SelectedNames(IntPtr hwnd) {
        var a=Read(hwnd);try{var names=new string[a.accChildCount];int count=0;for(int i=1;i<=a.accChildCount;i++)
            if((Convert.ToInt32(a.get_accState(i))&2)!=0)names[count++]=a.get_accName(i);Array.Resize(ref names,count);return names;
        }finally{Marshal.ReleaseComObject(a);}
    }
}
'@
