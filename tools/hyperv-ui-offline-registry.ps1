# Use the Windows offline registry library without attaching guest hives to HKLM.
if (-not ('AgwintermOfflineHive' -as [type])) {
Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class AgwintermOfflineHive {
    [DllImport("offreg.dll",CharSet=CharSet.Unicode)] static extern uint OROpenHive(string path,out IntPtr key);
    [DllImport("offreg.dll",CharSet=CharSet.Unicode)] static extern uint ORCreateKey(IntPtr parent,string name,string cls,uint options,IntPtr security,out IntPtr key,out uint disposition);
    [DllImport("offreg.dll",CharSet=CharSet.Unicode)] static extern uint ORSetValue(IntPtr key,string name,uint type,byte[] data,uint size);
    [DllImport("offreg.dll",CharSet=CharSet.Unicode)] static extern uint ORSaveHive(IntPtr key,string path,uint major,uint minor);
    [DllImport("offreg.dll")] static extern uint ORCloseHive(IntPtr key);
    [DllImport("offreg.dll")] static extern uint ORCloseKey(IntPtr key);
    [DllImport("offreg.dll",CharSet=CharSet.Unicode)] static extern uint ORGetValue(IntPtr key,string subkey,string name,out uint type,byte[] data,ref uint size);
    public static string ReadString(string path,string subkey,string name){
        IntPtr hive; uint type,size=0;
        Check(OROpenHive(path,out hive));
        try{
            Check(ORGetValue(hive,subkey,name,out type,null,ref size));
            if(type!=1 && type!=2)throw new InvalidOperationException("Expected registry string");
            byte[] data=new byte[size];
            Check(ORGetValue(hive,subkey,name,out type,data,ref size));
            return System.Text.Encoding.Unicode.GetString(data).TrimEnd('\0');
        }finally{ORCloseHive(hive);}
    }
    static void Check(uint code){if(code!=0)throw new Win32Exception((int)code);}
    public static void MarkSystemBcd(string source,string destination){
        IntPtr hive,key; uint disposition;
        Check(OROpenHive(source,out hive));
        try {
            Check(ORCreateKey(hive,"Description",null,0,IntPtr.Zero,out key,out disposition));
            try {
                Check(ORSetValue(key,"System",4,BitConverter.GetBytes(1),4));
                Check(ORSetValue(key,"TreatAsSystem",4,BitConverter.GetBytes(1),4));
            }finally{ORCloseKey(key);}
            Check(ORSaveHive(hive,destination,6,1));
        }finally{ORCloseHive(hive);}
    }
}
"@
}
