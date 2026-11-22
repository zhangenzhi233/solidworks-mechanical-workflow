using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using Environment=System.Environment;
public class StartCar {
    [STAThread] static int Main(string[] args){
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
            string file=new AssemblyName(e.Name).Name+".dll";
            var roots=new System.Collections.Generic.List<string>();
            foreach(var p in Process.GetProcessesByName("SLDWORKS")){try{roots.Add(Path.GetDirectoryName(p.MainModule.FileName));}catch{}}
            string reg=Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\SolidWorks\SOLIDWORKS 2024\Setup","SolidWorks Folder",null) as string;
            if(reg!=null)roots.Add(reg);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"SOLIDWORKS Corp\SOLIDWORKS"));
            foreach(string r in roots){string path=Path.Combine(r,"api","redist",file);if(File.Exists(path))return Assembly.LoadFrom(path);}
            return null;
        };
        bool first;
        using(var once=new System.Threading.Mutex(true,@"Local\SWPublicM1zhangenzhi233",out first)){
            if(!first){MessageBox.Show("整车手推面板已经打开。","M1 四轮车");return 0;}
            try{return Run(args);}catch(Exception ex){MessageBox.Show(ex.Message,"M1 四轮车");return 1;}
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int Run(string[] a){
        string root=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string path=a.FirstOrDefault(x=>x.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase))??Path.Combine(root,"原生装配","EX-M1-整车行驶试验.SLDASM");
        var car=new CarCAD(path);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string log=a.Contains("--log")?a[Array.IndexOf(a,"--log")+1]:null;
        var panel=new HandPanel(car,log);
        Application.Run(panel);return 0;
    }
}
