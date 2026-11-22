using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using Environment=System.Environment;

// mm, s, kg, N. The workpiece translates along the vise screw axis.
public class Bench {
    public const double Mass=.005184, Width=12, Lead=2, Radius=18, Restitution=.1;
    public double Gap=22, X=5, V, Clock, HandX=5, PulseAge=1, JawTarget=22, HandForce;
    public bool Touch;
    public int Hits;
    public double Peak, FirstHit=-1;
    public void Step(double h) {
        double oldGap=Gap;
        Gap=Math.Max(Width,Math.Min(32,JawTarget));
        double jawSpeed=(Gap-oldGap)/h;
        double force=0;
        if(Touch) {
            force=Math.Max(-.04,Math.Min(.04,(HandX-X)*.011-V*.00036));
        } else if(PulseAge<.016) {
            double u=Math.Max(0,Math.Min(1,(PulseAge+h*.5-.014)/.002));
            force=-.020*(1-u*u*(3-2*u));
        }
        HandForce=force;
        double a=1000*force/Mass, next=X+V*h+.5*a*h*h;
        V+=a*h;
        bool hit=false;
        if(next<=0) {
            hit=V<-.05; next=0;
            if(V<0)V=(Math.Abs(V)<.1||force<0)?0:-Restitution*V;
        }
        if(next>=Gap-Width) {
            hit=hit||V>jawSpeed+.05; next=Gap-Width;
            if(V>jawSpeed)V=jawSpeed-Restitution*(V-jawSpeed);
        }
        if(Gap<=Width+.000001) {next=0;V=0;}
        if(hit) {Hits++;if(FirstHit<0)FirstHit=Clock+h;}
        X=next; Clock+=h; PulseAge+=h;
        Peak=Math.Max(Peak,Math.Abs(V));
    }
    public void Advance(double seconds) {
        int n=(int)Math.Ceiling(seconds/.00025);
        if(n<1)return;
        double h=seconds/n, start=Gap, end=JawTarget;
        for(int i=1;i<=n;i++) {JawTarget=start+(end-start)*i/n;Step(h);}
        JawTarget=end;
    }
}

public class ViseModel {
    public ISldWorks Sw; public IModelDoc2 Doc; public string PathName;
    public double LastGap=double.NaN, LastX=double.NaN;
    public ViseModel(string file) {
        PathName=Path.GetFullPath(file);
        Sw=(ISldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application.32",true));
        int e=0,w=0;
        string directory=Path.GetDirectoryName(PathName),previous=Sw.GetCurrentWorkingDirectory();
        try {
            Sw.SetCurrentWorkingDirectory(directory);
            Doc=(IModelDoc2)Sw.GetOpenDocumentByName(PathName)??(IModelDoc2)Sw.OpenDoc6(PathName,2,65,"",ref e,ref w);
            if(Doc!=null)((IAssemblyDoc)Doc).ResolveAllLightWeightComponents(false);
        } finally {if(!string.IsNullOrEmpty(previous))Sw.SetCurrentWorkingDirectory(previous);}
        if(Doc==null||e!=0||!String.Equals(Doc.GetPathName(),PathName,StringComparison.OrdinalIgnoreCase))throw new Exception("台钳文件打开失败 "+e);
        var refs=((object[])((IAssemblyDoc)Doc).GetComponents(false)).Cast<IComponent2>();
        foreach(var c in refs){var part=c.GetModelDoc2() as IModelDoc2;
            if(part==null||!String.Equals(Path.GetDirectoryName(part.GetPathName()),directory,StringComparison.OrdinalIgnoreCase))
                throw new Exception("请先关闭已打开的另一份台钳及其同名零件，再打开本目录的实验。");}
        Sw.ActivateDoc3(Doc.GetTitle(),false,0,ref e);
        Doc.ShowConfiguration2("手推受力");
        if(Doc.ConfigurationManager.ActiveConfiguration.Name!="手推受力")throw new Exception("缺少交互配置");
        Sw.Visible=true;Sw.UserControl=true;
        ((IModelView)Doc.ActiveView).FrameState=1;
        Doc.ShowNamedView2("",7);Doc.ViewZoomtofit2();Doc.GraphicsRedraw2();
    }
    public void Set(Bench b,bool force=false) {
        if(!force&&Math.Abs(LastGap-b.Gap)<.001&&Math.Abs(LastX-b.X)<.001)return;
        if(force||!(Math.Abs(LastGap-b.Gap)<.00005)) Dim("开度加 10 mm",b.Gap);
        if(force||!(Math.Abs(LastX-b.X)<.00005)) Dim("试块位置加 10 mm",b.X);
        if(!Doc.EditRebuild3())throw new Exception("装配重建失败");
        Doc.GraphicsRedraw2();LastGap=b.Gap;LastX=b.X;
    }
    void Dim(string name,double x) {
        var dim=(IDimension)Doc.Parameter("D1@"+name);
        if(dim==null||dim.SetSystemValue3((x+10)*.001,1,null)!=0)throw new Exception("原生尺寸更新失败 "+name);
    }
    public double[] Actual(){
        var cs=((object[])((IAssemblyDoc)Doc).GetComponents(true)).Cast<IComponent2>().ToArray();
        Func<string,double[]> m=s=>(double[])cs.Single(c=>c.Name2.Contains(s)).Transform2.ArrayData;
        double[] s1=m("21-滑台"),r=m("22-手轮"),p=m("12毫米");
        return new double[]{s1[9]*1000,Math.Atan2(r[5],r[4])*180/Math.PI,p[9]*1000};
    }
    public double[] Axis() {
        var util=(IMathUtility)Sw.GetMathUtility();
        var tr=((IModelView)Doc.ActiveView).Transform;
        Func<double,double[]> p=x=>(double[])((IMathPoint)((IMathPoint)util.CreatePoint(new double[]{x,0,0})).MultiplyTransform(tr)).ArrayData;
        var a=p(0);var b=p(.001);return new double[]{b[0]-a[0],b[1]-a[1]};
    }
    public bool IsActive() {
        var d=Sw.ActiveDoc as IModelDoc2;
        return d!=null&&String.Equals(d.GetPathName(),PathName,StringComparison.OrdinalIgnoreCase);
    }
}

class Panel : Form {
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rectangle rect);
    readonly ViseModel model;
    Bench b=new Bench();
    readonly Timer poll=new Timer();
    readonly Stopwatch watch=Stopwatch.StartNew();
    readonly Label value=new Label(), message=new Label();
    readonly RadioButton block=new RadioButton(),jaw=new RadioButton(),wheel=new RadioButton();
    bool dragging,nativeDown;
    Mouse swMouse;
    Point nativePoint;

    public string LogPath;
    public void SelectJaw(){jaw.Checked=true;}
    public void SelectWheel(){wheel.Checked=true;}
    public void SelectBlock(){block.Checked=true;}
    void Log(string line){if(LogPath!=null)File.AppendAllText(LogPath,DateTime.Now.ToString("HH:mm:ss.fff")+" "+line+"\n");}
    Point start;
    double first,scaleX,scaleY,previous;
    public Panel(ViseModel m) {
        model=m; model.Set(b,true);
        Text="台钳手推实验";FormBorderStyle=FormBorderStyle.FixedToolWindow;StartPosition=FormStartPosition.Manual;
        var owner=new IntPtr(((IFrame)m.Sw.Frame()).GetHWnd());Rectangle host;GetWindowRect(owner,out host);
        var screen=Screen.FromHandle(owner).WorkingArea;
        Location=new Point(Math.Max(screen.Left,host.Left+18),Math.Min(screen.Bottom-250,Math.Max(screen.Top,host.Top+165)));ClientSize=new Size(312,213);
        Font=new Font("Microsoft YaHei UI",9);ShowInTaskbar=true;
        var title=new Label(){Text="选择要用手操作的零件",AutoSize=true,Left=12,Top=10};Controls.Add(title);
        int left=12;foreach(var pair in new[]{Tuple.Create(block,"蓝试块"),Tuple.Create(jaw,"活动钳口"),Tuple.Create(wheel,"手轮")}){var r=pair.Item1;r.Text=pair.Item2;r.Left=left;r.Top=35;r.Width=92;left+=95;Controls.Add(r);}
        jaw.Checked=true;
        swMouse=(Mouse)((IModelView)m.Doc.ActiveView).GetMouse();
        swMouse.MouseLBtnDownNotify+=NativeDown;
        swMouse.MouseMoveNotify+=NativeMove;
        swMouse.MouseLBtnUpNotify+=NativeUp;
        message.SetBounds(12,65,292,41);message.Text="在装配图中按住左键，沿钳口开合方向拖动。松手可观察惯性和接触。";Controls.Add(message);
        Action mode=()=>{message.Text=wheel.Checked?"在图中按住手轮，向上或向下擦动轮缘，带动丝杆。":"在装配图中按住左键，沿钳口开合方向拖动。松手可观察惯性和接触。";};
        jaw.CheckedChanged+=(o,e)=>mode();wheel.CheckedChanged+=(o,e)=>mode();
        var push=new Button(){Text="短推 0.020 N",Left=12,Top=108,Width=140,Height=29};
        push.Click+=(o,e)=>{ResetBench();b.PulseAge=0;};Controls.Add(push);
        var reset=new Button(){Text="复位",Left=162,Top=108,Width=100,Height=29};
        reset.Click+=(o,e)=>ResetBench();Controls.Add(reset);
        value.SetBounds(12,146,291,62);Controls.Add(value);
        poll.Interval=25;poll.Tick+=(o,e)=>TickBench();
        Shown+=(o,e)=>{previous=watch.Elapsed.TotalSeconds;poll.Start();UpdateValue();};
        FormClosed+=(o,e)=>{poll.Stop();swMouse.MouseLBtnDownNotify-=NativeDown;swMouse.MouseMoveNotify-=NativeMove;swMouse.MouseLBtnUpNotify-=NativeUp;
            if(model.IsActive()){model.Set(new Bench(),true);model.Doc.ShowConfiguration2("原生自由拖动");model.Doc.GraphicsRedraw2();}};
    }
    void Begin(Point pt){
        var dirs=model.Axis();scaleX=dirs[0];scaleY=dirs[1];
        if(scaleX*scaleX+scaleY*scaleY>.05){dragging=true;start=pt;first=block.Checked?b.X:b.Gap;Log("press "+pt+" axis="+scaleX+","+scaleY);}
    }
    int NativeDown(int x,int y,int w){nativePoint=new Point(x,y);nativeDown=true;if(model.IsActive())Begin(nativePoint);return 0;}
    int NativeMove(int x,int y,int w){nativePoint=new Point(x,y);if(nativeDown){ApplyInput(nativePoint);}return 0;}
    int NativeUp(int x,int y,int w){nativePoint=new Point(x,y);if(nativeDown){ApplyInput(nativePoint);AdvanceToNow();Log("release opening="+b.Gap+" x="+b.X+" v="+b.V);}nativeDown=false;dragging=false;b.Touch=false;return 0;}
    void ApplyInput(Point pt){
        if(!dragging)return;
        double px=pt.X-start.X,py=pt.Y-start.Y,den=scaleX*scaleX+scaleY*scaleY;
        double mm=(px*scaleX+py*scaleY)/den;
        if(block.Checked){b.Touch=true;b.HandX=first+mm;}
        if(jaw.Checked)b.JawTarget=Math.Max(12,Math.Min(32,first+mm));
        if(wheel.Checked)b.JawTarget=Math.Max(12,Math.Min(32,first-py/Math.Sqrt(den)*Bench.Lead/(2*Math.PI*Bench.Radius)));
        Log("viewport "+pt+" targetGap="+b.JawTarget.ToString("F5"));
    }
    void AdvanceToNow(){double now=watch.Elapsed.TotalSeconds,dt=Math.Min(.12,Math.Max(0,now-previous));previous=now;b.Advance(dt*.25);}
    void ResetBench(){b=new Bench();model.Set(b,true);previous=watch.Elapsed.TotalSeconds;UpdateValue();}
    void UpdateValue(){bool left=b.X<.003,right=b.Gap-Bench.Width-b.X<.003;string contact=left?(right?"两侧夹持":"固定钳口"):(right?"活动钳口":"无");
        value.Text=String.Format("钳口 {0:F2} mm　试块离固定钳口 {1:F2} mm\n速度 {2:F2} mm/s　推力 {3:F3} N\n接触位置　{4}",b.Gap,b.X,b.V,b.HandForce,contact);}
    void TickBench(){
        try {
            if(!model.IsActive()){dragging=false;nativeDown=false;b.Touch=false;previous=watch.Elapsed.TotalSeconds;return;}
            // Slow time in the viewport lets the first impact and rebound be seen.
            AdvanceToNow();
            model.Set(b);UpdateValue();
        } catch(Exception ex){poll.Stop();message.Text=ex.Message;Log(ex.ToString());}
    }
}

class Owner : IWin32Window { public IntPtr Handle{get;private set;} public Owner(IntPtr handle){Handle=handle;} }
class Launcher {
    static string here=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    [STAThread] static int Main(string[] args) {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
            string name=new AssemblyName(e.Name).Name+".dll";
            var paths=new System.Collections.Generic.List<string>();
            foreach(var proc in Process.GetProcessesByName("SLDWORKS")){try{paths.Add(Path.GetDirectoryName(proc.MainModule.FileName));}catch{}}
            string reg=Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\SolidWorks\SOLIDWORKS 2024\Setup","SolidWorks Folder",null) as string;
            if(reg!=null)paths.Add(reg);
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"SOLIDWORKS Corp\SOLIDWORKS"));
            foreach(string root in paths){string loc=Path.Combine(root,"api","redist",name);if(File.Exists(loc))return Assembly.LoadFrom(loc);}
            return null;
        };
        bool first;
        using(var once=new System.Threading.Mutex(true,@"Local\SWPublicVisezhangenzhi233",out first)){
            if(!first){if(!args.Contains("--test"))MessageBox.Show("手推面板已在 SOLIDWORKS 窗口左侧打开。","台钳");return 0;}
            try{return Run(args);}catch(Exception ex){if(args.Contains("--test"))Console.Error.WriteLine(ex);else MessageBox.Show(ex.Message,"台钳");return 1;}
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int Run(string[] args) {
        string file=args.FirstOrDefault(s=>s.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase))??Path.Combine(here,"原生装配","EX-小台钳-手动与受力.SLDASM");
        var model=new ViseModel(file);
        if(args.Contains("--test")){
            var bench=new Bench();model.Set(bench,true);
            var values=new System.Collections.Generic.List<string>{"time_s,opening_mm,block_face_mm,velocity_mm_s,SW_opening_mm,SW_wheel_deg,SW_block_face_mm"};
            Action record=()=>{model.Set(bench,true);var a=model.Actual();if(Math.Abs(a[0]-bench.Gap)>.002||Math.Abs(a[2]-bench.X)>.002||Math.Abs(Math.IEEERemainder(a[1]+180*(bench.Gap-22),360))>.05)throw new Exception("原生读数不同步");
                values.Add(string.Join(",",new double[]{bench.Clock,bench.Gap,bench.X,bench.V,a[0],a[1],a[2]}.Select(v=>v.ToString("G12",CultureInfo.InvariantCulture))));};
            record();bench.PulseAge=0;
            for(int j=1;j<=40;j++){bench.Advance(.005);record();}
            if(bench.FirstHit<.090||bench.FirstHit>.100||bench.Peak<56||bench.Peak>59||bench.V<=0)throw new Exception("独立碰撞验证失败");
            var stat=string.Format(CultureInfo.InvariantCulture,"impact={0:F5} s; peak={1:F5} mm/s; v_after={2:F5} mm/s",bench.FirstHit,bench.Peak,bench.V);
            bench.JawTarget=22.5;bench.Advance(.025);record();
            bench.JawTarget=15;bench.Advance(.25);record();bench.JawTarget=12;bench.Advance(.25);record();
            if(bench.X!=0||bench.Gap!=12)throw new Exception("夹持尺寸不符");
            Console.WriteLine(stat+"; viewport pixels/mm "+string.Join(",",model.Axis()));
            File.WriteAllLines(args[Array.IndexOf(args,"--test")+1],values);
            model.Set(new Bench(),true);model.Doc.ShowConfiguration2("原生自由拖动");return 0;
        }
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        var ui=new Panel(model);
        if(args.Contains("--jaw"))ui.SelectJaw();
        if(args.Contains("--wheel"))ui.SelectWheel();
        if(args.Contains("--block"))ui.SelectBlock();
        if(args.Contains("--log"))ui.LogPath=args[Array.IndexOf(args,"--log")+1];
        ui.Show(new Owner(new IntPtr(((IFrame)model.Sw.Frame()).GetHWnd())));
        Application.Run(ui);return Environment.ExitCode;
    }
}
